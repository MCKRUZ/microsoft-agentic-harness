namespace Domain.AI.Egress;

/// <summary>
/// One declarative entry in a per-skill egress allowlist. Exactly one of
/// <see cref="Host"/> or <see cref="HostPattern"/> is set; the policy refuses
/// to match against an entry that violates that invariant.
/// </summary>
/// <remarks>
/// <para>
/// Allowlist semantics are deliberately narrow because a permissive regex on
/// the host portion of an allowlist is itself an SSRF vector — a `.*` pattern
/// authored by mistake reduces the allowlist to a no-op. <see cref="HostPattern"/>
/// therefore supports a single wildcard at the leftmost DNS label only:
/// <c>*.azure-api.net</c> is accepted; <c>api.*.com</c>, <c>**.foo.com</c>, and
/// any regex metacharacter beyond the leading <c>*.</c> are rejected at policy
/// construction time.
/// </para>
/// <para>
/// <see cref="Schemes"/> and <see cref="Ports"/> further narrow the match. The
/// policy compares case-insensitively for scheme and exact-equal for port.
/// Empty <see cref="Schemes"/> or empty <see cref="Ports"/> mean "no value
/// matches" — entries with empty arrays match nothing and are useful only as
/// explicit "disabled" placeholders. Callers who want any port should declare
/// the ports they actually need rather than leaving the list empty.
/// </para>
/// </remarks>
public sealed record EgressAllowlistEntry
{
    /// <summary>The exact hostname this entry matches. Null when <see cref="HostPattern"/> is set.</summary>
    public string? Host { get; init; }

    /// <summary>
    /// A leftmost-label wildcard pattern such as <c>*.azure-api.net</c>. Null when
    /// <see cref="Host"/> is set. Only the leading <c>*.</c> is supported; the
    /// remainder must be a literal DNS suffix with at least one dot.
    /// </summary>
    public string? HostPattern { get; init; }

    /// <summary>The set of URI schemes this entry matches. Compared case-insensitively. Empty means "matches nothing".</summary>
    public IReadOnlyList<string> Schemes { get; init; } = [];

    /// <summary>The set of remote ports this entry matches. Compared exact-equal. Empty means "matches nothing".</summary>
    public IReadOnlyList<int> Ports { get; init; } = [];
}
