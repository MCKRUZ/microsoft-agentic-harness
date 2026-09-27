namespace Presentation.AgentHub.Config;

/// <summary>
/// Configuration for the AgentHub presentation host.
/// Bound from <c>AppConfig:AgentHub</c> in appsettings.json.
/// </summary>
public sealed record AgentHubConfig
{
    /// <summary>Name of the default agent used when no agent is specified.</summary>
    public string DefaultAgentName { get; init; } = string.Empty;

    /// <summary>Maximum number of conversation messages dispatched to the agent per turn.</summary>
    public int MaxHistoryMessages { get; init; } = 20;

    /// <summary>
    /// Minutes of inactivity before an idle session is automatically completed.
    /// </summary>
    public int SessionIdleTimeoutMinutes { get; init; } = 5;

    /// <summary>
    /// Seconds a server-side client round-trip tool (e.g. <c>dashboard_control</c>) will block
    /// awaiting the browser's result before failing the call. Bounds the AG-UI blocking-proxy
    /// pattern so a disconnected or unresponsive client never parks an agent run. Must stay below
    /// the agent turn timeout so the tool fails gracefully rather than the whole turn timing out.
    /// </summary>
    public int ClientToolTimeoutSeconds { get; init; } = 120;

    /// <summary>CORS configuration for this host.</summary>
    public AgentHubCorsConfig Cors { get; init; } = new();

    /// <summary>
    /// Overrides SignalR's own 32KB default cap on a single hub-method invocation payload, in
    /// bytes. Leave unset (<see langword="null"/>) to keep that 32KB default — a consumer sending
    /// legitimately large per-call content (e.g. a full persona/system-prompt override via
    /// <c>SetConversationSettings</c>) raises this rather than needing a code change. Deliberately
    /// <em>not</em> forwarded to SignalR's own <see langword="null"/> ("no limit") when unset:
    /// this stays a bounded override, never an accidental removal of the cap.
    /// </summary>
    public long? SignalRMaxReceiveMessageSizeBytes { get; init; }
}
