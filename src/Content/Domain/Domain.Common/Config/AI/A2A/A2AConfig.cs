namespace Domain.Common.Config.AI.A2A;

/// <summary>
/// Configuration for Agent-to-Agent protocol. Bound from AppConfig:AI:A2A.
/// </summary>
public class A2AConfig
{
    /// <summary>Whether A2A protocol is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL for this agent's A2A endpoint.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Name of this agent as advertised in its agent card.</summary>
    public string AgentName { get; set; } = "AgenticHarness";

    /// <summary>Description for this agent's card.</summary>
    public string AgentDescription { get; set; } = "Microsoft Agentic Harness POC agent.";

    /// <summary>Remote agent endpoints to discover and communicate with.</summary>
    public List<RemoteAgentEndpoint> RemoteAgents { get; set; } = [];

    /// <summary>Whether A2A is configured with at least one remote agent.</summary>
    public bool HasRemoteAgents => RemoteAgents.Count > 0;
}

/// <summary>
/// A remote agent endpoint for A2A communication.
/// </summary>
public class RemoteAgentEndpoint
{
    /// <summary>Display name of the remote agent.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the remote agent's A2A endpoint.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional API key for authentication with this remote agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WARNING:</strong> This value must NEVER be stored in appsettings.json or any file
    /// committed to source control. Use User Secrets (development) or Azure Key Vault (production).
    /// </para>
    /// </remarks>
    public string? ApiKey { get; set; }
}
