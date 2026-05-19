using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Signals that quality drift was detected at warning severity.
/// The agent's output quality has deviated from baseline but not critically.
/// </summary>
public sealed record DriftWarnEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured (e.g. "Agent", "Skill").</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
}

/// <summary>
/// Signals that quality drift was detected at alert severity.
/// Includes the baseline ID for correlation with baseline store records.
/// </summary>
public sealed record DriftAlertEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }
}

/// <summary>
/// Signals that quality drift was detected at escalation severity.
/// An escalation request has been triggered and is awaiting human review.
/// </summary>
public sealed record DriftEscalateEvent : AgUiEvent
{
    /// <summary>The scope level at which drift was measured.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    /// <summary>Identifier of the entity within the scope.</summary>
    [JsonPropertyName("scopeIdentifier")]
    public required string ScopeIdentifier { get; init; }

    /// <summary>Per-dimension deviation values keyed by dimension name.</summary>
    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, double> Dimensions { get; init; }

    /// <summary>Maximum deviation across all dimensions (sigma units).</summary>
    [JsonPropertyName("maxDeviation")]
    public required double MaxDeviation { get; init; }

    /// <summary>Severity classification string.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>The baseline this score was compared against.</summary>
    [JsonPropertyName("baselineId")]
    public required string BaselineId { get; init; }

    /// <summary>
    /// Escalation correlation ID. Empty when the escalation has not yet been queued;
    /// clients correlate drift-escalate events with escalation-requested events by timestamp and scope.
    /// </summary>
    [JsonPropertyName("escalationId")]
    public required string EscalationId { get; init; }
}

/// <summary>
/// Signals that a previously detected drift has been resolved through
/// learning application, baseline adjustment, manual dismissal, or escalation resolution.
/// </summary>
public sealed record DriftResolvedEvent : AgUiEvent
{
    /// <summary>The drift event that was resolved.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary>How the drift was resolved (e.g. "LearningApplied", "BaselineAdjusted").</summary>
    [JsonPropertyName("resolutionType")]
    public required string ResolutionType { get; init; }

    /// <summary>Identifier of the resolving entity (learning ID, escalation ID, etc.).</summary>
    [JsonPropertyName("resolvedBy")]
    public required string ResolvedBy { get; init; }

    /// <summary>When the drift was resolved.</summary>
    [JsonPropertyName("resolvedAt")]
    public required DateTimeOffset ResolvedAt { get; init; }
}
