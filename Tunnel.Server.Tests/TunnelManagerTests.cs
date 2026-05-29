using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tunnel.Shared.Messages;
using HttpResponseMessage = Tunnel.Shared.Messages.HttpResponseMessage;

namespace Tunnel.Server.Tests
{
    public class TunnelManagerTests
    {
        [Fact]
        public void RegisterAgent_StoresSocketAndRetrievesIt()
        {
            // Arrange
            var manager = new TunnelManager();
            var mockSocket = new Mock<WebSocket>();

            // Act
            manager.RegisterAgent("agent-1", mockSocket.Object);
            var retrievedSocket = manager.GetAgentSocket("agent-1");

            // Assert
            Assert.NotNull(retrievedSocket);
            Assert.Same(mockSocket.Object, retrievedSocket);
        }

        [Fact]
        public void RegisterAgent_ExistingAgent_ClosesOldSocketGracefully()
        {
            // Arrange
            var manager = new TunnelManager();
            var mockSocket1 = new Mock<WebSocket>();
            mockSocket1.SetupGet(x => x.State).Returns(WebSocketState.Open);

            var mockSocket2 = new Mock<WebSocket>();

            // Act
            manager.RegisterAgent("agent-1", mockSocket1.Object);
            manager.RegisterAgent("agent-1", mockSocket2.Object); // Should replace socket 1

            // Assert
            // Verify the first socket was told to close cleanly
            mockSocket1.Verify(x => x.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Replaced",
                It.IsAny<CancellationToken>()), Times.Once);

            // Verify the dictionary now holds the second socket
            var retrievedSocket = manager.GetAgentSocket("agent-1");
            Assert.Same(mockSocket2.Object, retrievedSocket);
        }

        [Fact]
        public void UnregisterAgent_RemovesSocket()
        {
            // Arrange
            var manager = new TunnelManager();
            var mockSocket = new Mock<WebSocket>();
            manager.RegisterAgent("agent-1", mockSocket.Object);

            // Act
            manager.UnregisterAgent("agent-1");

            // Assert
            Assert.Null(manager.GetAgentSocket("agent-1"));
        }

        [Fact]
        public async Task StartListeningAsync_MatchesResponseToWaitingThread()
        {
            // Arrange
            var manager = new TunnelManager();
            var mockSocket = new Mock<WebSocket>();
            var requestId = Guid.NewGuid();

            // 1. Setup a thread waiting for an HTTP response
            var pendingResponse = manager.CreatePendingResponse(requestId, CancellationToken.None);

            // 2. Create the mock message the Agent would send back
            var agentResponse = new HttpResponseMessage { RequestId = requestId, StatusCode = 200 };
            var tunnelMsg = TunnelMessage.Create(MessageType.HttpResponse, agentResponse);
            var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tunnelMsg, TunnelMessage.SerializerOptions));

            mockSocket.SetupGet(x => x.State).Returns(WebSocketState.Open);

            // 3. Mock the WebSocket to push our fake message into the listening loop
            int callCount = 0;
            mockSocket.Setup(x => x.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken ct) =>
                {
                    if (callCount++ == 0)
                    {
                        // First receive call: yield the JSON response
                        messageBytes.CopyTo(buffer.Array, buffer.Offset);
                        return new WebSocketReceiveResult(messageBytes.Length, WebSocketMessageType.Text, true);
                    }
                    else
                    {
                        // Second call: yield a close frame to exit the infinite listening loop
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                    }
                });

            // Act
            await manager.StartListeningAsync("agent-1", mockSocket.Object);

            // Assert
            // The StartListeningAsync loop should have parsed the JSON and resolved our pending response!
            Assert.True(pendingResponse.HeadersTcs.Task.IsCompletedSuccessfully);

            var resolvedResponse = await pendingResponse.HeadersTcs.Task;
            Assert.Equal(200, resolvedResponse.StatusCode);
            Assert.Equal(requestId, resolvedResponse.RequestId);
        }

        [Fact]
        public async Task CreatePendingResponse_TimesOut_WhenTokenCanceled()
        {
            // Arrange
            var manager = new TunnelManager();
            var requestId = Guid.NewGuid();

            // Set up a token that simulates the 30-second timeout we put in Program.cs
            using var cts = new CancellationTokenSource();

            // Act
            var pending = manager.CreatePendingResponse(requestId, cts.Token);

            // Simulate the timeout expiring
            cts.Cancel();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.HeadersTcs.Task);
        }
    }
}
