using Microsoft.Extensions.Options;
using Serilog;

namespace Tunnel.Agent;

class Program
    {
    static async Task Main(string[] args)
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("======================================");
            Log.Information("   Tunnel Local Agent Starting...   ");
            Log.Information("======================================");

            var options = new AgentOptions
            {
                LocalServerUrl = args.Length >= 1 ? args[0] : "http://localhost:5000",
                RelayServerUrl = args.Length >= 2 ? args[1] : "ws://localhost:5188/connect",
                AgentId = args.Length >= 3 ? args[2] : "my-local-machine",
                ApiKey = args.Length >= 4 ? args[3] : "my-super-secret-key"
            };

            Log.Information("Target Local Server : {LocalServerUrl}", options.LocalServerUrl);
            Log.Information("Relay Tunnel Server : {RelayServerUrl}", options.RelayServerUrl);
            Log.Information("Agent ID            : {AgentId}", options.AgentId);

            var services = new ServiceCollection();

            // Add Serilog to the DI container
            services.AddLogging(builder => builder.AddSerilog(dispose: true));

            services.AddSingleton(Options.Create(options));
            services.AddSingleton<IWebSocketClient, DefaultWebSocketClient>();
            services.AddHttpClient<AgentClient>();

            var serviceProvider = services.BuildServiceProvider();

            var agentClient = serviceProvider.GetRequiredService<AgentClient>();
            await agentClient.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Agent crashed.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}