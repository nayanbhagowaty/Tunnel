using System.Net.WebSockets;

namespace Tunnel.Agent
{
    /// <summary>
    /// The production implementation of the WebSocket client.
    /// Handles thread-safe sending and instance recreation upon reconnection.
    /// </summary>
    public class DefaultWebSocketClient : IWebSocketClient
    {
        private ClientWebSocket _ws = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocketState State => _ws.State;

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            // If reconnecting, we need a fresh ClientWebSocket instance
            if (_ws.State != WebSocketState.None && _ws.State != WebSocketState.Closed)
            {
                _ws.Dispose();
                _ws = new ClientWebSocket();
            }

            await _ws.ConnectAsync(uri, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return _ws.ReceiveAsync(buffer, cancellationToken);
        }

        public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // Replaces the manual lock() with an async-friendly SemaphoreSlim
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _ws?.Dispose();
            _sendLock?.Dispose();
        }
    }
}
