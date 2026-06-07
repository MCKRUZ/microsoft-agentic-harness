namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the per-skill outbound egress layer (PR-3b).
/// Bound from <c>AppConfig:AI:Egress</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// The layer is off by default (<see cref="Enabled"/> is false). When enabled,
/// the harness's named <c>HttpClient</c> ("egress") composes an outer
/// allowlist-checking <c>DelegatingHandler</c> on top of an inner
/// <c>Microsoft.Security.AntiSSRF</c> handler that enforces RFC 1918 /
/// link-local / loopback / IMDS denies and connect-time DNS validation. Both
/// rings must agree for a request to leave the process.
/// </para>
/// <para>
/// <see cref="DefaultAllowlist"/> is intentionally empty by default. PR-3c
/// wires per-skill allowlists from the skill manifest; until then,
/// <see cref="DefaultAllowlist"/> is the single source of truth and an empty
/// list means default-deny — every request blocked.
/// </para>
/// </remarks>
public sealed class EgressConfig
{
    /// <summary>
    /// Master toggle. When false, the named <c>HttpClient</c> ("egress") still
    /// registers but the startup validator skips its checks and consumers
    /// should not route any traffic through it. When true, the named client
    /// enforces the full handler chain and the startup validator runs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directory path for the JSONL egress-decision audit. Relative paths
    /// resolve from the application working directory. Same shape as the
    /// existing change-proposal and drift audit paths.
    /// </summary>
    public string AuditStoragePath { get; set; } = ".agent-sessions/egress";

    /// <summary>
    /// When false (default), plain-text <c>http://</c> targets are blocked at
    /// the inner SSRF handler. Setting true outside Development is rejected by
    /// the startup validator unless explicitly opted in — every plain-text
    /// request becomes a credential-leak vector in any environment with a
    /// real network adversary.
    /// </summary>
    public bool AllowPlainTextHttp { get; set; }

    /// <summary>
    /// When false (default), <see cref="AllowPlainTextHttp"/> = true outside
    /// Development is fatal at boot. Set true only when the consumer has
    /// accepted the trade-off (e.g. a fully-internal trial behind mTLS).
    /// </summary>
    public bool AllowPlainTextHttpOutsideDevelopment { get; set; }

    /// <summary>
    /// The default allowlist applied when no skill-specific allowlist is
    /// registered. Empty by default — the layer is default-deny and a consumer
    /// who enables it without supplying entries blocks every outbound request.
    /// PR-3c will source per-skill overrides from the skill manifest; this
    /// list remains the fallback.
    /// </summary>
    public List<EgressAllowlistConfigEntry> DefaultAllowlist { get; set; } = [];
}

/// <summary>
/// Configuration shape for a single egress allowlist entry. Mirrored at the
/// domain layer by <see cref="Domain.AI.Egress.EgressAllowlistEntry"/>; the
/// infrastructure layer maps config entries onto domain entries at startup.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="Host"/> or <see cref="HostPattern"/> is set;
/// startup validation rejects entries that violate the invariant. Wildcards in
/// <see cref="HostPattern"/> are limited to the leftmost label (e.g.
/// <c>*.azure-api.net</c>); the policy layer rejects anything more permissive
/// because a regex on the host portion of an allowlist is itself an SSRF
/// vector.
/// </para>
/// </remarks>
public sealed class EgressAllowlistConfigEntry
{
    /// <summary>The exact hostname this entry matches. Null when <see cref="HostPattern"/> is set.</summary>
    public string? Host { get; set; }

    /// <summary>The leftmost-label wildcard pattern (e.g. <c>*.azure-api.net</c>). Null when <see cref="Host"/> is set.</summary>
    public string? HostPattern { get; set; }

    /// <summary>The set of URI schemes this entry matches. Compared case-insensitively. Empty means "matches nothing".</summary>
    public List<string> Schemes { get; set; } = [];

    /// <summary>The set of remote ports this entry matches. Compared exact-equal. Empty means "matches nothing".</summary>
    public List<int> Ports { get; set; } = [];
}
