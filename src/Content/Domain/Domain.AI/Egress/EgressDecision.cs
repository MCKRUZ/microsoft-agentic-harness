namespace Domain.AI.Egress;

/// <summary>
/// The verdict produced by an <see cref="IEgressPolicy"/> for a single outbound
/// HTTP request. One <see cref="EgressDecision"/> per <c>SendAsync</c> attempt
/// — the harness audit pipeline writes exactly one JSONL line per decision so
/// the resulting log can be replayed end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// The decision is the public contract between the harness's per-skill egress
/// policy (<see cref="IEgressPolicy"/>) and the audit + telemetry layers. The
/// inner SSRF defense (currently <c>Microsoft.Security.AntiSSRF</c>) does NOT
/// produce an <see cref="EgressDecision"/>; it throws on violation, and the
/// delegating handler maps the throw into a separate audit record. The decision
/// type captures only the harness's own allowlist verdict.
/// </para>
/// <para>
/// All fields are immutable. <see cref="MatchedAllowlistEntry"/> is the
/// <c>host</c> or <c>hostPattern</c> string from the matched
/// <see cref="EgressAllowlistEntry"/> when <see cref="Allowed"/> is true, and
/// null when the decision is deny. <see cref="FinalIpAddress"/> is populated
/// only when the policy resolved the host as part of the decision; the SSRF
/// layer performs its own connect-time resolution independently.
/// </para>
/// </remarks>
public sealed record EgressDecision
{
    /// <summary>The verdict — true means the request may proceed to the SSRF layer; false means blocked at the allowlist gate.</summary>
    public required bool Allowed { get; init; }

    /// <summary>Short human-readable explanation captured in the audit. Stable enough to be machine-readable for dashboards.</summary>
    public required string Reason { get; init; }

    /// <summary>The matched <c>host</c> or <c>hostPattern</c> from the allowlist when <see cref="Allowed"/> is true; null on deny.</summary>
    public string? MatchedAllowlistEntry { get; init; }

    /// <summary>
    /// The resolved IPv4/IPv6 address used by the policy at decision time. Null when the
    /// policy did not need DNS resolution to reach a verdict (e.g. denied at scheme check)
    /// or when DNS resolution itself failed.
    /// </summary>
    public string? FinalIpAddress { get; init; }

    /// <summary>The exact <see cref="Uri"/> evaluated. Captured into the audit verbatim — query strings and fragments included for replayability.</summary>
    public required Uri Target { get; init; }

    /// <summary>UTC timestamp at which the policy reached the verdict.</summary>
    public required DateTimeOffset DecidedAt { get; init; }
}
