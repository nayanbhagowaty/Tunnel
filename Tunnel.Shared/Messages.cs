using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tunnel.Shared.Messages
{
    /// <summary>
    /// Source Generator context to support AOT compilation and trimming.
    /// </summary>
    [JsonSerializable(typeof(TunnelMessage))]
    [JsonSerializable(typeof(RegisterAgentRequest))]
    [JsonSerializable(typeof(RegisterAgentResponse))]
    [JsonSerializable(typeof(HttpRequestMessage))]
    [JsonSerializable(typeof(HttpResponseMessage))]
    [JsonSerializable(typeof(DataChunkMessage))]
    public partial class TunnelJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Identifies the type of message being sent over the WebSocket.
    /// </summary>
    public enum MessageType
    {
        RegisterAgentRequest,
        RegisterAgentResponse,
        HttpRequest,
        HttpResponse,
        DataChunk // Included for Phase 4 (Streaming)
    }

    /// <summary>
    /// The base wrapper envelope for all communication over the tunnel.
    /// </summary>
    public class TunnelMessage
    {
        public MessageType Type { get; set; }
        public string Payload { get; set; } = string.Empty;

        // Provide standard options utilizing the source generator
        public static readonly JsonSerializerOptions SerializerOptions = new()
        {
            TypeInfoResolver = TunnelJsonContext.Default
        };

        /// <summary>
        /// Helper to package a specific message object into a TunnelMessage.
        /// </summary>
        public static TunnelMessage Create<T>(MessageType type, T payload)
        {
            return new TunnelMessage
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload, typeof(T), SerializerOptions)
            };
        }

        /// <summary>
        /// Helper to unpackage the payload back into a specific C# object.
        /// </summary>
        public T? GetPayload<T>()
        {
            return (T?)JsonSerializer.Deserialize(Payload, typeof(T), SerializerOptions);
        }
    }

    // ==========================================
    // Specific Message Payloads
    // ==========================================

    /// <summary>
    /// Sent by the Agent to identify itself to the Relay Server.
    /// </summary>
    public class RegisterAgentRequest
    {
        public string AgentId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty; // For Phase 5 Security
    }

    /// <summary>
    /// Sent by the Server back to the Agent to confirm connection status.
    /// </summary>
    public class RegisterAgentResponse
    {
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents an incoming HTTP request from a browser, packaged by the Server.
    /// </summary>
    public class HttpRequestMessage
    {
        // CRUCIAL: RequestId maps the eventual response back to the correct browser request
        public Guid RequestId { get; set; }
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = string.Empty;
        public Dictionary<string, string[]> Headers { get; set; } = new();
        public byte[]? Body { get; set; }
    }

    /// <summary>
    /// Represents the local Web Server's response, packaged by the Agent to send back.
    /// </summary>
    public class HttpResponseMessage
    {
        // Matches the RequestId of the HttpRequestMessage
        public Guid RequestId { get; set; }
        public int StatusCode { get; set; }
        public Dictionary<string, string[]> Headers { get; set; } = new();
        public byte[]? Body { get; set; }
    }

    /// <summary>
    /// (Phase 4) Used to stream large bodies chunk by chunk instead of holding them in memory.
    /// </summary>
    public class DataChunkMessage
    {
        public Guid RequestId { get; set; }
        public byte[] Chunk { get; set; } = Array.Empty<byte>();
        public bool IsLastChunk { get; set; }
    }
}
