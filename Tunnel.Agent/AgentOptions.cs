
namespace Tunnel.Agent
{
    /// <summary>
    /// Defines the configuration properties needed for the agent.
    /// </summary>
    public interface IAgentOptions
    {
        string LocalServerUrl { get; set; }
        string RelayServerUrl { get; set; }
        string AgentId { get; set; }
        string ApiKey { get; set; }
    }

    /// <summary>
    /// Encapsulates the configuration needed for the agent.
    /// </summary>
    public class AgentOptions : IAgentOptions
    {
        public string LocalServerUrl { get; set; } = string.Empty;
        public string RelayServerUrl { get; set; } = string.Empty;
        public string AgentId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }
}
