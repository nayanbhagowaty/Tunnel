using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tunnel.Server.Tests
{
    public class RoutingMiddlewareTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public RoutingMiddlewareTests(WebApplicationFactory<Program> factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task CatchAllRoute_MissingAgentIdHeader_ReturnsBadRequest()
        {
            // Arrange
            var client = _factory.CreateClient();

            // Act
            var response = await client.GetAsync("/any-path");

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("Missing X-Tunnel-Agent-Id header", content);
        }

        [Fact]
        public async Task CatchAllRoute_AgentOffline_ReturnsBadGateway()
        {
            // Arrange
            var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, "/any-path");
            request.Headers.Add("X-Tunnel-Agent-Id", "offline-agent");

            // Act
            var response = await client.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("Agent 'offline-agent' is offline", content);
        }
    }
}
