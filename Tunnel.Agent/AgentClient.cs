using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tunnel.Shared.Messages;

namespace Tunnel.Agent
{
    public class AgentClient
    {
        private readonly HttpClient _httpClient;
        private readonly IWebSocketClient _webSocket;
        private readonly AgentOptions _options;
        private readonly ILogger<AgentClient> _logger;

        public AgentClient(HttpClient httpClient, IWebSocketClient webSocket, IOptions<AgentOptions> options, ILogger<AgentClient> logger)
        {
            _httpClient = httpClient;
            _webSocket = webSocket;
            _options = options.Value;
            _logger = logger;

            _options.LocalServerUrl = _options.LocalServerUrl.TrimEnd('/');
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Connecting to Relay Server at {RelayUrl}...", _options.RelayServerUrl);

                    await _webSocket.ConnectAsync(new Uri(_options.RelayServerUrl), CancellationToken.None);
                    _logger.LogInformation("Connected! Authenticating...");

                    var regReq = new RegisterAgentRequest { AgentId = _options.AgentId, ApiKey = _options.ApiKey };
                    var regMsg = TunnelMessage.Create(MessageType.RegisterAgentRequest, regReq);
                    await SendMessageAsync(regMsg);

                    await ReceiveLoopAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Graceful exit triggered by cancellation token
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connection error. Retrying in 5 seconds...");
                    try
                    {
                        await Task.Delay(5000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 8];

            while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Server closed connection.");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                var tunnelMessage = JsonSerializer.Deserialize<TunnelMessage>(messageJson, TunnelMessage.SerializerOptions);

                if (tunnelMessage?.Type == MessageType.HttpRequest)
                {
                    var httpRequest = tunnelMessage.GetPayload<Shared.Messages.HttpRequestMessage>();
                    if (httpRequest != null)
                    {
                        _ = ProcessRequestAsync(httpRequest);
                    }
                }
                else if (tunnelMessage?.Type == MessageType.RegisterAgentResponse)
                {
                    _logger.LogInformation("Successfully registered with the Relay Server!");
                }
            }
        }

        private async Task ProcessRequestAsync(Shared.Messages.HttpRequestMessage requestMsg)
        {
            _logger.LogInformation("Forwarding {Method} {Url}", requestMsg.Method, requestMsg.Url);

            var targetUrl = $"{_options.LocalServerUrl}{requestMsg.Url}";
            var localHttpRequest = new System.Net.Http.HttpRequestMessage(new HttpMethod(requestMsg.Method), targetUrl);

            if (requestMsg.Body != null && requestMsg.Body.Length > 0)
            {
                localHttpRequest.Content = new ByteArrayContent(requestMsg.Body);
            }

            foreach (var header in requestMsg.Headers)
            {
                if (!localHttpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    localHttpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            var responseMsg = new Shared.Messages.HttpResponseMessage { RequestId = requestMsg.RequestId };

            try
            {
                using var localHttpResponse = await _httpClient.SendAsync(localHttpRequest, HttpCompletionOption.ResponseHeadersRead);
                responseMsg.StatusCode = (int)localHttpResponse.StatusCode;

                foreach (var header in localHttpResponse.Headers.Concat(localHttpResponse.Content.Headers))
                {
                    responseMsg.Headers[header.Key] = header.Value.ToArray();
                }

                var tunnelMsg = TunnelMessage.Create(MessageType.HttpResponse, responseMsg);
                await SendMessageAsync(tunnelMsg);

                using var stream = await localHttpResponse.Content.ReadAsStreamAsync();
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    var chunkMsg = new DataChunkMessage
                    {
                        RequestId = requestMsg.RequestId,
                        Chunk = buffer.AsSpan(0, bytesRead).ToArray(),
                        IsLastChunk = false
                    };
                    await SendMessageAsync(TunnelMessage.Create(MessageType.DataChunk, chunkMsg));
                }

                await SendMessageAsync(TunnelMessage.Create(MessageType.DataChunk, new DataChunkMessage { RequestId = requestMsg.RequestId, IsLastChunk = true }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error forwarding to local server.");
                responseMsg.StatusCode = 502;

                await SendMessageAsync(TunnelMessage.Create(MessageType.HttpResponse, responseMsg));
                var errorBytes = Encoding.UTF8.GetBytes("Local server unavailable.");
                await SendMessageAsync(TunnelMessage.Create(MessageType.DataChunk, new DataChunkMessage { RequestId = requestMsg.RequestId, Chunk = errorBytes, IsLastChunk = true }));
            }
        }

        private async Task SendMessageAsync(TunnelMessage message)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, TunnelMessage.SerializerOptions));
            // Thread safety is now delegated to the IWebSocketClient implementation
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
