namespace Domain.Common.Config.AI.BundleExecution;

/// <summary>
/// Authentication configuration for the standalone <c>Presentation.BundleApi</c> host — the HTTP front door
/// through which an external system registers and runs agent bundles. Bound from
/// <c>AppConfig:AI:BundleExecution:Auth</c>.
/// </summary>
/// <remarks>
/// <para>
/// The bundle API runs externally-authored agents, so it is deliberately isolated behind its <em>own</em>
/// authentication audience — never sharing a credential surface with the MCP server or the agent hub. This
/// config carries the Entra ID identifiers that identify that audience.
/// </para>
/// <para>
/// <strong>Fail-closed.</strong> The host refuses to start unless either a valid scheme is configured
/// (<see cref="IsConfigured"/>) or a developer has consciously opted into anonymous serving
/// (<see cref="AllowAnonymous"/>). Running under <c>Environment=Development</c> alone never disables
/// authentication — the opt-in is explicit and logged loudly at startup.
/// </para>
/// </remarks>
public sealed class BundleApiAuthConfig
{
    /// <summary>
    /// The Entra ID tenant whose tokens the host accepts. Combined with <see cref="ClientId"/> to validate
    /// the issuer and audience of inbound bearer tokens. Required for a configured scheme.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The Entra ID application (client) id that identifies this API's audience. Inbound tokens must target
    /// <c>api://&lt;ClientId&gt;</c>. This is the host's own audience — distinct from every other service's.
    /// Required for a configured scheme.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Explicit opt-in that lets the host serve anonymously when no scheme is configured. Default
    /// <c>false</c> — the host is fail-closed and refuses to start without authentication in every
    /// environment. A developer must consciously set this for local work, and the host logs a prominent
    /// warning at startup while it is on.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Whether a token-validation scheme is configured (both tenant and client id supplied).</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>
    /// Whether exactly one of <see cref="TenantId"/>/<see cref="ClientId"/> is supplied — a half-configured
    /// scheme. This is treated as a misconfiguration the host refuses to start on, rather than silently
    /// falling through to the anonymous path: an operator who set a tenant plainly intended real auth, so a
    /// forgotten client id (or vice versa) must fail loudly, not boot the door open.
    /// </summary>
    public bool IsPartiallyConfigured =>
        !IsConfigured
        && (!string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(ClientId));
}
