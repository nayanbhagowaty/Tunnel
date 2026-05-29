using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Tunnel.Server;
using Tunnel.Shared.Messages;
using HttpRequestMessage = Tunnel.Shared.Messages.HttpRequestMessage;

// 1. Configure Serilog Bootstrap Logging
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting MyTunnel Server...");

    var builder = WebApplication.CreateBuilder(args);

    // 2. Wire up Serilog to the ASP.NET Core host
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddSingleton<TunnelManager>();

    var app = builder.Build();

    // Log HTTP requests
    app.UseSerilogRequestLogging();
    app.UseWebSockets();

    // ==========================================
    // Endpoint 1: The WebSocket Tunnel Connection
    // ==========================================
    app.Map("/connect", async (HttpContext context, TunnelManager tunnelManager) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

    // Wait for the Agent to send the RegisterAgentRequest as the first message
    var buffer = new byte[1024 * 4];
    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
    var initMessage = JsonSerializer.Deserialize<TunnelMessage>(messageJson, TunnelMessage.SerializerOptions);

    if (initMessage?.Type != MessageType.RegisterAgentRequest)
    {
        await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected Registration", CancellationToken.None);
        return;
    }

    var regRequest = initMessage.GetPayload<RegisterAgentRequest>();
    var agentId = regRequest?.AgentId;

    if (string.IsNullOrEmpty(agentId))
    {
        await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid Agent ID", CancellationToken.None);
        return;
    }

    // --- PHASE 5 SECURITY CHECK ---
    // In a production scenario, you would inject IConfiguration and read this from appsettings.json
    var expectedApiKey = "my-super-secret-key";

    if (regRequest?.ApiKey != expectedApiKey)
    {
        Console.WriteLine($"[Security] Rejected agent '{agentId}' due to invalid API Key.");
        // 4001 is a custom WebSocket status code indicating unauthorized/policy violation
        await webSocket.CloseAsync((WebSocketCloseStatus)4001, "Unauthorized - Invalid API Key", CancellationToken.None);
        return;
    }
    // ------------------------------

    // Acknowledge successful registration
    var responseMsg = TunnelMessage.Create(MessageType.RegisterAgentResponse, new RegisterAgentResponse { IsSuccess = true });
    var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseMsg, TunnelMessage.SerializerOptions));
    await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);

    // Register and start listening for traffic
    tunnelManager.RegisterAgent(agentId, webSocket);
    await tunnelManager.StartListeningAsync(agentId, webSocket);
});

// ==========================================
// Endpoint 2: Catch-All HTTP Interceptor
// ==========================================
app.Map("/{**catch-all}", async (HttpContext context, TunnelManager tunnelManager) =>
{
    // For testing locally, we'll route based on a custom header.
    // In production, you'd usually extract this from the Host header (e.g., agent1.mytunnel.com)
    if (!context.Request.Headers.TryGetValue("X-Tunnel-Agent-Id", out var agentIdValues))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing X-Tunnel-Agent-Id header.");
        return;
    }

    var agentId = agentIdValues.ToString();
    var socket = tunnelManager.GetAgentSocket(agentId);

    if (socket == null || socket.State != WebSocketState.Open)
    {
        context.Response.StatusCode = 502; // Bad Gateway
        await context.Response.WriteAsync($"Agent '{agentId}' is offline.");
        return;
    }

    // 1. Pack the incoming HTTP Request
    var requestId = Guid.NewGuid();
    var httpRequestMessage = new HttpRequestMessage
    {
        RequestId = requestId,
        Method = context.Request.Method,
        Url = context.Request.Path + context.Request.QueryString,
    };

    // Copy headers (ignoring Host and our custom routing header)
    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("X-Tunnel-Agent-Id", StringComparison.OrdinalIgnoreCase)) continue;

        httpRequestMessage.Headers[header.Key] = header.Value.ToArray();
    }

    // Copy Body if present
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        httpRequestMessage.Body = ms.ToArray();
    }

    // 2. Prepare to wait for the response (30 second timeout)
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var pendingResponse = tunnelManager.CreatePendingResponse(requestId, cts.Token);

    // 3. Send the packed request down the WebSocket to the Agent
    var tunnelMsg = TunnelMessage.Create(MessageType.HttpRequest, httpRequestMessage);
    var tunnelMsgBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tunnelMsg, TunnelMessage.SerializerOptions));

    try
    {
        await socket.SendAsync(new ArraySegment<byte>(tunnelMsgBytes), WebSocketMessageType.Text, true, cts.Token);
    }
    catch (Exception)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsync("Failed to send request to agent.");
        return;
    }

    // 4. Wait for the Agent to reply
    try
    {
        var agentResponse = await pendingResponse.HeadersTcs.Task;

        // 5. Unpack the response headers and write it to the actual browser/client
        context.Response.StatusCode = agentResponse.StatusCode;
        foreach (var header in agentResponse.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        // 6. Stream the body chunks
        await foreach (var chunk in pendingResponse.ChunkChannel.Reader.ReadAllAsync(context.RequestAborted))
        {
            if (chunk.Chunk.Length > 0)
            {
                await context.Response.Body.WriteAsync(chunk.Chunk, context.RequestAborted);
            }
            if (chunk.IsLastChunk) break;
        }
    }
    catch (OperationCanceledException)
    {
        context.Response.StatusCode = 504; // Gateway Timeout
        await context.Response.WriteAsync("Agent timed out.");
    }
});

app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
public partial class Program { }