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

    /// <summary>
    /// PR-7 surface configuration. Controls which transport
    /// (in-process vs cross-process) the harness A2A client/server use, and
    /// the auth settings for the cross-process transport.
    /// </summary>
    public A2ASurfaceConfig Surface { get; set; } = new();
}

/// <summary>
/// PR-7 A2A surface configuration. Bound from <c>AppConfig:AI:A2A:Surface</c>.
/// </summary>
public class A2ASurfaceConfig
{
    /// <summary>
    /// Transport selector. <c>InProcess</c> routes calls through the in-process
    /// bridge; <c>Http</c> routes calls over HTTP with mutual TLS and a workload
    /// identity JWT bearer. Default is <c>InProcess</c> so the example demo
    /// works out of the box without external setup.
    /// </summary>
    public A2ATransport Transport { get; set; } = A2ATransport.InProcess;

    /// <summary>
    /// Required audience claim ("aud") for inbound JWTs on the cross-process
    /// server. Typically this agent's Entra application uri. Null when the
    /// transport is <see cref="A2ATransport.InProcess"/>.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Expected issuer claim ("iss") for inbound JWTs. Typically the harness
    /// identity provider's discovery endpoint. Null in the in-process transport.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    /// Clock skew tolerance in seconds when validating JWT expiry. Set to 0 on
    /// the cross-process path per the security playbook — never widen this
    /// without a recorded design decision.
    /// </summary>
    public int ClockSkewSeconds { get; set; }

    /// <summary>
    /// Maximum number of extension headers permitted on a single envelope. Caps
    /// the server's deserialisation work and shields against malicious payload
    /// inflation. Defaults to 16.
    /// </summary>
    public int MaxExtensionHeaders { get; set; } = 16;
}

/// <summary>
/// Transport selector for the PR-7 A2A surface.
/// </summary>
public enum A2ATransport
{
    /// <summary>Same-process dispatch via the in-process bridge.</summary>
    InProcess = 0,

    /// <summary>Cross-process dispatch over HTTP with mTLS + workload-identity JWT.</summary>
    Http = 1
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
