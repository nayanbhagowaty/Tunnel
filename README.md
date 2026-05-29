<div align="center">

# 🧊 NeonTunnel 
**The Cybernetic Reverse Proxy & Webhook Uplink for .NET**

[![.NET](https://img.shields.io/badge/.NET-9.0%20%7C%2010.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-00E5FF?style=for-the-badge)](https://opensource.org/licenses/MIT)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-FF007F?style=for-the-badge)](#contributing)

*Bypass firewalls, expose local servers to the grid, and securely multiplex network traffic—all in pure C#.*

</div>

---

### Don't go by name yet! ;-)

**NeonTunnel** is a high-performance, open-source **ngrok alternative** and **reverse tunneling tool** written entirely in C#. Designed for developers who need to expose local development environments, APIs, or autonomous agents to the public internet without opening inbound firewall ports or battling complex NAT configurations.

By establishing a persistent, encrypted outbound WebSocket connection from your local machine to a public relay, NeonTunnel creates a holographic routing layer that shuttles HTTP traffic seamlessly across the network.

## ⚡ Key Features

* **Inbound Port Bypass:** Operates purely on outbound WebSocket connections (WSS), seamlessly traversing corporate firewalls and strict NATs.
* **Asynchronous Multiplexing:** Handles hundreds of concurrent HTTP requests over a single TCP socket without head-of-line blocking.
* **Zero-Memory Streaming:** Native support for chunked transfer encoding via `System.Threading.Channels`. Stream massive files or video data without exhausting agent RAM.
* **AOT-Ready Serialization:** Reflection-free JSON serialization via .NET Source Generators for blazing-fast, low-footprint execution.
* **Edge Authentication:** Built-in API key validation drops unauthorized connection attempts at the perimeter.

---

## 🛠️ Architecture & Components

NeonTunnel is built on a tripartite architecture designed for modularity and speed.

### 1. The Relay Server (`NeonTunnel.Server`)
The public-facing uplink. Hosted on a VPS or cloud provider, this ASP.NET Core application binds to public ports (80/443). 
* **The Function:** It acts as a traffic director. When a public HTTP request arrives, the server captures the headers and body, identifies the target agent via subdomain or routing headers, and injects the request down the established WebSocket tunnel.
* **Under the Hood:** Utilizes `TaskCompletionSource` to asynchronously pause the incoming web request thread while waiting for the remote agent to stream the response back.

### 2. The Edge Agent (`NeonTunnel.Agent`)
A lightweight, headless .NET worker service running on your restricted local machine.
* **The Function:** Maintains a resilient, self-healing WebSocket connection to the Relay Server. When it receives a packaged HTTP request from the grid, it reconstitutes it, fires it at your local `localhost` server, and pipes the response back up the tunnel.
* **Under the Hood:** Built with robust retry-policies and cancellation-token architecture to survive network drops.

### 3. The Telemetry Protocol (`NeonTunnel.Shared`)
The shared binary-JSON vocabulary between the Server and the Agent.
* **The Function:** Wraps standard HTTP components (Verbs, URLs, Headers, Bodies) into structured envelopes (`HttpRequestMessage`, `DataChunkMessage`). 
* **Under the Hood:** Every request is stamped with a unique `RequestId` (GUID), allowing the multiplexing of highly concurrent traffic.

---

## 🚀 Use Cases

* **Webhook Development:** Instantly test webhooks from Stripe, GitHub, or Discord against your local development environment.
* **Portable Dev Environments:** Expose local servers running on mobile coding setups (like Samsung DeX with Linux containers) to external collaborators.
* **AI & Agent Orchestration:** Securely expose locally hosted LLMs (like Gemma) or autonomous RAG orchestration agents to public-facing frontends without exposing your private hardware.
* **IoT & Remote Access:** Maintain persistent access to administrative interfaces on remote devices hidden behind carrier-grade NAT.

---

## 💻 Getting Started

### Prerequisites
* .NET 10.0 SDK (or newer)
* A public server/VPS (for hosting the Relay)

### Step 1: Spin up the Relay Server
Deploy the server to your public infrastructure. Provide an SSL certificate via a reverse proxy like Nginx or Caddy.

```bash
# Clone the repository
git clone [https://github.com/nayanbhagowaty/Tunnel](https://github.com/nayanbhagowaty/Tunnel)
cd NeonTunnel/src/NeonTunnel.Server

# Run the server
dotnet run --urls "[http://0.0.0.0:5045](http://0.0.0.0:5045)"
```

### Step 2: Boot the Local Agent
On your restricted local machine, point the agent to your local web server and the public relay.

```bash
cd ../NeonTunnel.Agent

# Arguments: <LocalTarget> <RelayWebSocketUrl> <AgentId> <ApiKey>
dotnet run "http://localhost:5000" "ws://your-public-server:5045/connect" "my-edge-node" "super-secret-key"
```

### Step 3: Access your Local Server
Send an HTTP request to your public relay server, specifying the target agent. The relay will tunnel it directly to your localhost:5000.
```bash
curl -H "X-Tunnel-Agent-Id: my-edge-node" http://your-public-server:5045/api/data
```

### 🔮 Future Enhancements
The grid is always expanding. Here is the roadmap for future cybernetic upgrades:

[ ] YARP Integration: Replace the custom HTTP interceptor with Microsoft's YARP (Yet Another Reverse Proxy) for enterprise-grade load balancing, header manipulation, and subdomain-based routing (e.g., agent1.neontunnel.com).

[ ] Automatic Let's Encrypt Provisioning: Native ACME integration on the Relay Server to automatically mint and renew SSL certificates for agent subdomains.

[ ] Glassmorphic Web Dashboard: A Blazor-based UI with icy-cyan accents and live traffic inspection, allowing developers to replay failed webhook requests directly from the browser.

[ ] TCP/UDP Port Forwarding: Expand beyond HTTP/HTTPS to support raw TCP/UDP tunneling for database connections and SSH access.

[ ] End-to-End Encryption (E2EE): Encrypt the HTTP payloads on the Agent before tunneling, ensuring the Relay Server operates in a "zero-knowledge" capacity.

### 🤝 Contributing
Contributions from the open-source community are what make the network stronger.

Fork the Project

Create your Feature Branch (git checkout -b feature/HolographicUI)

Commit your Changes (git commit -m 'Add some HolographicUI')

Push to the Branch (git push origin feature/HolographicUI)

Open a Pull Request

📜 License
Distributed under the MIT License. See LICENSE for more information.