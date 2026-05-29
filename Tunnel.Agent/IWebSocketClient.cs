using System.Net.WebSockets;

namespace Tunnel.Agent
{
    /// <summary>
    /// Abstracts the ClientWebSocket to allow for mocking in Unit Tests.
    /// </summary>
    public interface IWebSocketClient : IDisposable
    {
        WebSocketState State { get; }
        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
        Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);
        Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
    }
}
