namespace Domain.Common.Constants;

/// <summary>
/// Shared result-cap bounds for the audit-chain query APIs added in #714 (governance, change,
/// egress, escalation) — the same numbers <c>DriftValidationRules</c> already uses for drift's
/// own audit query, kept here (Domain layer, zero framework dependencies) rather than duplicated
/// per trail, since the invariant (bound response size and memory on an append-only,
/// unbounded-length log) is identical across all of them. Lives in Domain rather than
/// <c>Application.Core</c> so it stays visible from <c>Application.AI.Common</c> too, whose
/// existing CQRS (e.g. Change) predates and lives outside <c>Application.Core</c>.
/// </summary>
public static class AuditQueryDefaults
{
    /// <summary>Hard ceiling on records returned by one audit query.</summary>
    public const int MaxResults = 1000;

    /// <summary>Default cap applied when a caller does not specify a smaller one.</summary>
    public const int DefaultResults = 500;
}
