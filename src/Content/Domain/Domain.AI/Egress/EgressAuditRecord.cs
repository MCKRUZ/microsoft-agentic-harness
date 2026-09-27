namespace Domain.AI.Egress;

/// <summary>
/// A single egress policy decision, serialized as one hash-chained JSONL line in the egress
/// audit trail (<c>egress.jsonl</c>). Promoted from a private nested type inside
/// <c>JsonlEgressAuditWriter</c> so it can be returned by a query API (#714) — the same shape
/// that has always been written to disk, unchanged, so existing audit files keep deserializing.
/// </summary>
/// <remarks>
/// Every decision is captured regardless of verdict — allows and denies alike. An audit limited
/// to denies would hide the silent expansion of a skill's outbound surface area over time.
/// </remarks>
public sealed record EgressAuditRecord
{
    /// <summary>When this egress decision was made.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Whether the request was allowed.</summary>
    public required bool Allowed { get; init; }

    /// <summary>The full target URI, as text.</summary>
    public required string Target { get; init; }

    /// <summary>The target host.</summary>
    public required string Host { get; init; }

    /// <summary>The target URI scheme (e.g. <c>"https"</c>).</summary>
    public required string Scheme { get; init; }

    /// <summary>The target port.</summary>
    public required int Port { get; init; }

    /// <summary>The policy's reason for the decision.</summary>
    public required string Reason { get; init; }

    /// <summary>The allowlist entry that matched, if the decision was based on one.</summary>
    public string? MatchedAllowlistEntry { get; init; }

    /// <summary>The resolved IP address the connection would use, if known at decision time.</summary>
    public string? FinalIpAddress { get; init; }

    /// <summary>The agent identity that submitted the request.</summary>
    public required EgressAuditIdentity AgentIdentity { get; init; }
}
