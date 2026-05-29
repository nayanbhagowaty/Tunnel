using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Tunnel.Shared.Messages;
using HttpResponseMessage = Tunnel.Shared.Messages.HttpResponseMessage;

namespace Tunnel.Server
{
    public class PendingResponse
    {
        public TaskCompletionSource<HttpResponseMessage> HeadersTcs { get; } = new();
        public Channel<DataChunkMessage> ChunkChannel { get; } = Channel.CreateUnbounded<DataChunkMessage>();
    }

    public class TunnelManager
    {
        // Tracks connected agents by their AgentId
        private readonly ConcurrentDictionary<string, WebSocket> _agents = new();

        // Tracks pending HTTP requests waiting for a response from an agent
        private readonly ConcurrentDictionary<Guid, PendingResponse> _pendingRequests = new();

        /// <summary>
        /// Registers a new agent WebSocket connection.
        /// </summary>
        public void RegisterAgent(string agentId, WebSocket socket)
        {
            _agents.AddOrUpdate(agentId, socket, (_, existingSocket) =>
            {
                // Close the old socket if the agent reconnects
                if (existingSocket.State == WebSocketState.Open)
                    existingSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced", CancellationToken.None);
                return socket;
            });
            Console.WriteLine($"[TunnelManager] Agent '{agentId}' registered.");
        }

        public void UnregisterAgent(string agentId)
        {
            if (_agents.TryRemove(agentId, out var socket))
            {
                Console.WriteLine($"[TunnelManager] Agent '{agentId}' disconnected.");
            }
        }

        public WebSocket? GetAgentSocket(string agentId)
        {
            _agents.TryGetValue(agentId, out var socket);
            return socket;
        }

        /// <summary>
        /// Creates a pending response context for streaming.
        /// </summary>
        public PendingResponse CreatePendingResponse(Guid requestId, CancellationToken cancellationToken)
        {
            var pending = new PendingResponse();

            // Handle timeout/cancellation
            cancellationToken.Register(() =>
            {
                pending.HeadersTcs.TrySetCanceled();
                _pendingRequests.TryRemove(requestId, out _);
            });

            _pendingRequests.TryAdd(requestId, pending);
            return pending;
        }

        /// <summary>
        /// Background loop that continuously reads messages from an Agent's WebSocket.
        /// </summary>
        public async Task StartListeningAsync(string agentId, WebSocket socket)
        {
            var buffer = new byte[1024 * 4];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    // Parse the message
                    var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                    var tunnelMessage = JsonSerializer.Deserialize<TunnelMessage>(messageJson, TunnelMessage.SerializerOptions);

                    if (tunnelMessage != null)
                    {
                        if (tunnelMessage.Type == MessageType.HttpResponse)
                        {
                            var response = tunnelMessage.GetPayload<HttpResponseMessage>();
                            if (response != null && _pendingRequests.TryGetValue(response.RequestId, out var pending))
                            {
                                pending.HeadersTcs.TrySetResult(response);
                            }
                        }
                        else if (tunnelMessage.Type == MessageType.DataChunk)
                        {
                            var chunk = tunnelMessage.GetPayload<DataChunkMessage>();
                            if (chunk != null && _pendingRequests.TryGetValue(chunk.RequestId, out var pending))
                            {
                                pending.ChunkChannel.Writer.TryWrite(chunk);
                                if (chunk.IsLastChunk)
                                {
                                    pending.ChunkChannel.Writer.TryComplete();
                                    _pendingRequests.TryRemove(chunk.RequestId, out _);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TunnelManager] Error listening to agent '{agentId}': {ex.Message}");
            }
            finally
            {
                UnregisterAgent(agentId);
            }
        }
    }
}
