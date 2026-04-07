namespace Domain.Common.Workflow;

/// <summary>
/// Result of evaluating a decision framework.
///
/// <para><b>Purpose:</b></para>
/// Represents the outcome of evaluating a validation gate or decision point.
/// Used by IDecisionEvaluator to return the results of rule evaluation.
///
/// <para><b>Example:</b></para>
/// <code>
/// var result = new DecisionResult
/// {
///     Outcome = "go",
///     Metadata = new Dictionary&lt;string, object&gt;
///     {
///         ["score"] = 92,
///         ["critical_issues"] = 0,
///         ["high_issues"] = 1
///     }
/// };
/// </code>
/// </summary>
public class DecisionResult
{
    /// <summary>
    /// The decision outcome determined by evaluating the decision framework.
    /// Examples: "go", "conditional_go", "no_go", "poc_required"
    /// Valid outcomes are defined in SKILL.md decision_framework.possible_outcomes
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable explanation for why this outcome was chosen.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Conditions that must be met for this outcome (for conditional outcomes).
    /// Example: "Fix critical issues before proceeding"
    /// </summary>
    public List<string> Conditions { get; set; } = new();

    /// <summary>
    /// All inputs and evaluated values from the decision.
    /// Includes scores, issue counts, and any other evaluated metrics.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// The specific rule that matched to produce this outcome.
    /// Null if no rule matched.
    /// </summary>
    public DecisionRule? MatchedRule { get; set; }

    /// <summary>
    /// Timestamp when the decision was made.
    /// </summary>
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this decision is considered successful/positive.
    /// Interpretation depends on context (e.g., "go" = true, "no_go" = false).
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Checks if the decision is a "go" outcome.
    /// </summary>
    public bool IsGo() => Outcome.Equals("go", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the decision is a "conditional_go" outcome.
    /// </summary>
    public bool IsConditionalGo() => Outcome.Equals("conditional_go", StringComparison.OrdinalIgnoreCase)
        || Outcome.Equals("conditional", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the decision is a "no_go" outcome.
    /// </summary>
    public bool IsNoGo() => Outcome.Equals("no_go", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if the decision allows proceeding (go or conditional_go).
    /// </summary>
    public bool CanProceed() => IsGo() || IsConditionalGo();

    /// <summary>
    /// Gets the validation score from metadata.
    /// </summary>
    public int GetScore()
    {
        if (Metadata.TryGetValue("score", out var value) && value is int score)
            return score;
        return 0;
    }

    /// <summary>
    /// Gets issue counts from metadata.
    /// </summary>
    public (int Critical, int High, int Medium, int Low) GetIssueCounts()
    {
        int critical = 0, high = 0, medium = 0, low = 0;

        if (Metadata.TryGetValue("critical_issues", out var c) && c is int ci)
            critical = ci;
        if (Metadata.TryGetValue("high_issues", out var h) && h is int hi)
            high = hi;
        if (Metadata.TryGetValue("medium_issues", out var m) && m is int mi)
            medium = mi;
        if (Metadata.TryGetValue("low_issues", out var l) && l is int li)
            low = li;

        return (critical, high, medium, low);
    }
}
