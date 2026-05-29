using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Tunnel.Shared.Messages; // Assumes your shared messages exist here
using Microsoft.Extensions.Logging.Abstractions;

namespace Tunnel.Agent.Tests
{
    public class AgentClientTests
    {
        private readonly Mock<IWebSocketClient> _mockWsClient;
        private readonly MockHttpMessageHandler _mockHttpHandler;
        private readonly HttpClient _httpClient;
        private readonly IOptions<AgentOptions> _options;

        public AgentClientTests()
        {
            _mockWsClient = new Mock<IWebSocketClient>();
            _mockHttpHandler = new MockHttpMessageHandler();
            _httpClient = new HttpClient(_mockHttpHandler);

            var agentOptions = new AgentOptions
            {
                LocalServerUrl = "http://localhost:5000",
                RelayServerUrl = "ws://localhost:5045/connect",
                AgentId = "test-agent",
                ApiKey = "secret-test-key"
            };
            _options = Options.Create(agentOptions);
        }

        [Fact]
        public async Task TC02_StartAsync_SendsCorrectRegistrationPayload()
        {
            // Arrange
            var agent = new AgentClient(_httpClient, _mockWsClient.Object, _options, NullLogger<AgentClient>.Instance);

            TunnelMessage capturedMessage = null;
            using var cts = new CancellationTokenSource();

            // 1. Capture the message sent over the WebSocket, then CANCEL the token to break the loop!
            _mockWsClient.Setup(x => x.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<WebSocketMessageType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<ArraySegment<byte>, WebSocketMessageType, bool, CancellationToken>((buffer, type, end, ct) =>
                {
                    var json = Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count);
                    capturedMessage = JsonSerializer.Deserialize<TunnelMessage>(json, TunnelMessage.SerializerOptions);

                    // Trigger cancellation to end the test gracefully
                    cts.Cancel();
                })
                .Returns(Task.CompletedTask);

            // 2. Prevent ReceiveAsync from spinning while cancellation processes
            _mockWsClient.Setup(x => x.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
                .Returns(async (ArraySegment<byte> buffer, CancellationToken ct) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Text, true);
                });

            // Act
            await agent.StartAsync(cts.Token);

            // Assert
            Assert.NotNull(capturedMessage);
            Assert.Equal(MessageType.RegisterAgentRequest, capturedMessage.Type);

            var payload = capturedMessage.GetPayload<RegisterAgentRequest>();
            Assert.NotNull(payload);
            Assert.Equal("test-agent", payload.AgentId);
            Assert.Equal("secret-test-key", payload.ApiKey);
        }

        [Fact]
        public async Task TC07_ReceiveLoop_ForwardsHttpRequestToLocalServer()
        {
            // Arrange
            var agent = new AgentClient(_httpClient, _mockWsClient.Object, _options, NullLogger<AgentClient>.Instance);
            var requestId = Guid.NewGuid();
            using var cts = new CancellationTokenSource();

            // Create a mock incoming HTTP request from the tunnel
            var incomingTunnelReq = new Shared.Messages.HttpRequestMessage
            {
                RequestId = requestId,
                Method = "POST",
                Url = "/api/data",
                Body = Encoding.UTF8.GetBytes("test-payload")
            };
            incomingTunnelReq.Headers.Add("X-Custom-Header", new[] { "TestValue" });

            var tunnelMessage = TunnelMessage.Create(MessageType.HttpRequest, incomingTunnelReq);
            var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tunnelMessage, TunnelMessage.SerializerOptions));

            // Setup WebSocket State so ReceiveLoop runs
            _mockWsClient.SetupGet(x => x.State).Returns(WebSocketState.Open);

            // Mock ReceiveAsync to return our fake request, then cancel the token to break the loop
            var receiveCallCount = 0;
            _mockWsClient.Setup(x => x.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ArraySegment<byte> buffer, CancellationToken ct) =>
                {
                    if (receiveCallCount++ == 0)
                    {
                        // First call: return the HTTP request message
                        messageBytes.CopyTo(buffer.Array, buffer.Offset);

                        // Give the background task a tiny window to fire before cancelling the loop
                        Task.Run(async () => {
                            await Task.Delay(100);
                            cts.Cancel();
                        });

                        return new WebSocketReceiveResult(messageBytes.Length, WebSocketMessageType.Text, true);
                    }
                    else
                    {
                        // Fallback block if loop spins again
                        throw new OperationCanceledException();
                    }
                });

            // Act
            await agent.StartAsync(cts.Token);

            // Wait for the fire-and-forget background task to actually hit the HttpClient
            var interceptedRequest = await _mockHttpHandler.WaitForRequestAsync(TimeSpan.FromSeconds(2));

            // Assert
            Assert.NotNull(interceptedRequest);
            Assert.Equal(HttpMethod.Post, interceptedRequest.Method);
            Assert.Equal("http://localhost:5000/api/data", interceptedRequest.RequestUri.ToString());

            // Verify Headers
            Assert.True(interceptedRequest.Headers.Contains("X-Custom-Header"));

            // Verify Body
            var bodyString = await interceptedRequest.Content.ReadAsStringAsync();
            Assert.Equal("test-payload", bodyString);
        }

        /// <summary>
        /// A helper class to mock HttpClient behavior and capture requests without hitting the network.
        /// </summary>
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly TaskCompletionSource<System.Net.Http.HttpRequestMessage> _tcs = new();

            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Read and replace content before the request/content is disposed, so the test can read it later
                if (request.Content != null)
                {
                    var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    var headers = request.Content.Headers.ToList();
                    var newContent = new System.Net.Http.ByteArrayContent(bodyBytes);
                    foreach (var h in headers)
                        newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    request.Content = newContent;
                }

                // Capture the request so the test can assert against it
                _tcs.TrySetResult(request);

                // Return a dummy 200 OK response to the AgentClient
                return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Mocked Response Body")
                };
            }

            public async Task<System.Net.Http.HttpRequestMessage> WaitForRequestAsync(TimeSpan timeout)
            {
                using var cts = new CancellationTokenSource(timeout);
                try
                {
                    // Await the task, but throw if the timeout is reached before the request comes through
                    var t = _tcs.Task;
                    if (await Task.WhenAny(t, Task.Delay(timeout, cts.Token)) == t)
                    {
                        return await t;
                    }
                    throw new TimeoutException("HTTP Request was not intercepted in time.");
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("HTTP Request was not intercepted in time.");
                }
            }
        }
    }
}
