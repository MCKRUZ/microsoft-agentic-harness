namespace Domain.Common.Workflow;

/// <summary>
/// Defines a single decision rule for evaluation.
///
/// <para><b>Purpose:</b></para>
/// Represents one condition-outcome pair in a decision framework.
/// Rules are evaluated in order; the first matching rule determines the outcome.
///
/// <para><b>Example:</b></para>
/// <code>
/// var rule = new DecisionRule
/// {
///     Condition = "score >= 85 AND critical_issues == 0",
///     Outcome = "go"
/// };
/// </code>
/// </summary>
public class DecisionRule
{
    /// <summary>
    /// The condition expression to evaluate.
    ///
    /// <para><b>Supported Syntax:</b></para>
    /// C# expression syntax with AND, OR, comparison operators, and parentheses.
    /// Variables are provided from the evaluation inputs.
    ///
    /// <para><b>Examples:</b></para>
    /// <list type="bullet">
    ///   <item><code>"score >= 85"</code></item>
    ///   <item><code>"score >= 85 AND critical_issues == 0"</code></item>
    ///   <item><code>"score >= 70 AND score < 85"</code></item>
    ///   <item><code>"(score >= 85 OR (score >= 70 AND critical_issues == 0)) AND high_issues <= 2"</code></item>
    /// </list>
    /// </summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>
    /// The outcome to return if this condition matches.
    /// Examples: "go", "conditional_go", "no_go"
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of when this rule applies.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional metadata associated with this rule.
    /// Can be used to store additional context about the rule.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Validates the rule and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Condition))
            errors.Add("Rule condition cannot be empty");

        if (string.IsNullOrWhiteSpace(Outcome))
            errors.Add("Rule outcome cannot be empty");

        return errors;
    }
}
