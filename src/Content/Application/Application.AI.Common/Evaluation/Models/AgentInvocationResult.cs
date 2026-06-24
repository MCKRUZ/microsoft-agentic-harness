namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// The result of invoking the harness for one evaluation case.
/// Produced by an <c>IAgentInvoker</c> and consumed by <c>IEvalMetric</c> implementations.
/// </summary>
/// <remarks>
/// <para>
/// Carries the agent's textual output plus optional retrieval context (for RAG metrics)
/// and cost data (for cost-tracking reports). Designed to be metric-agnostic — individual
/// metrics read whichever fields they need.
/// </para>
/// <para>
/// <see cref="Success"/> is false when the harness errored (e.g. timeout, network failure,
/// content-safety block). Metrics should still receive failed invocations so they can
/// score appropriately (e.g. a "no PII leak" metric should pass on a content-blocked response).
/// </para>
/// </remarks>
public sealed record AgentInvocationResult
{
    /// <summary>Whether the harness produced a response without errors.</summary>
    public required bool Success { get; init; }

    /// <summary>The agent's textual response. Empty string when <see cref="Success"/> is false.</summary>
    public required string Output { get; init; }

    /// <summary>
    /// Retrieved context surfaced by RAG, if any. Consumed by RAG quality metrics.
    /// Null when retrieval was not part of the invocation.
    /// </summary>
    public string? RetrievedContext { get; init; }

    /// <summary>Tool names invoked during the turn, in invocation order.</summary>
    public IReadOnlyList<string> ToolsInvoked { get; init; } = [];

    /// <summary>
    /// Diagnostic message when <see cref="Success"/> is false. Contains the handler's
    /// error string when one is provided; when the underlying handler returned an
    /// unsuccessful response with a body (e.g. a content-safety refusal text), that
    /// body is preserved here so the failure detail is not lost. Reporters and metrics
    /// should treat this as potentially multi-line, multi-KB text.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Input tokens consumed by the invocation.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens produced by the invocation.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total cost in USD of this invocation. Zero when cost tracking is unavailable.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>The model used for the invocation, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Wall-clock duration of the invocation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Snapshot of the per-invocation governance decisions the agent's tool calls passed through
    /// during the turn, carried straight from <c>AgentTurnResult.Governance</c>. Null when tool-invocation
    /// governance was not engaged (the legacy default). Lets governance-behaviour metrics grade what the
    /// live governed tool path actually did — allowing, denying, gating on approval, escalating — rather
    /// than scoring hand-authored payloads.
    /// </summary>
    public Domain.AI.Governance.GovernanceTrace? Governance { get; init; }
}
