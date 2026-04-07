namespace Domain.Common.Workflow;

/// <summary>
/// Defines a decision framework for evaluating validation gates or decision points.
///
/// <para><b>Purpose:</b></para>
/// Encapsulates decision logic that was previously hardcoded in the orchestrator.
/// Read from SKILL.md decision_framework section, making validation fully configurable.
///
/// <para><b>Example from SKILL.md (validation skill):</b></para>
/// <code>
/// decision_framework:
///   type: gate_decision
///   possible_outcomes:
///     - go
///     - conditional_go
///     - no_go
///
///   decision_rules:
///     - condition: "score >= 85 AND critical_issues == 0 AND high_issues <= 2"
///       outcome: go
///     - condition: "score >= 70 AND score < 85 AND critical_issues <= 1"
///       outcome: conditional_go
///     - condition: "score < 70 OR critical_issues > 1"
///       outcome: no_go
///
///   metadata_outputs:
///     - decision
///     - score
///     - critical_issues
///     - high_issues
///     - conditions
/// </code>
/// </summary>
public class DecisionFramework
{
    /// <summary>
    /// Type of decision framework.
    /// Examples: "gate_decision", "threshold", "rule_based", "llm_evaluated"
    /// </summary>
    public string Type { get; set; } = "gate_decision";

    /// <summary>
    /// All possible outcomes from this decision framework.
    /// Examples: ["go", "conditional_go", "no_go"]
    /// </summary>
    public List<string> PossibleOutcomes { get; set; } = new();

    /// <summary>
    /// Rules to evaluate in order to determine the outcome.
    /// The first rule whose condition evaluates to true determines the outcome.
    /// </summary>
    public List<DecisionRule> DecisionRules { get; set; } = new();

    /// <summary>
    /// Metadata keys that should be output from the decision evaluation.
    /// Examples: ["decision", "score", "critical_issues", "high_issues", "conditions"]
    /// </summary>
    public List<string> MetadataOutputs { get; set; } = new();

    /// <summary>
    /// Validates the decision framework and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Check that possible outcomes are not empty
        if (PossibleOutcomes.Count == 0)
            errors.Add("possible_outcomes cannot be empty");

        // Check that decision rules are not empty
        if (DecisionRules.Count == 0)
            errors.Add("decision_rules cannot be empty");

        // Check that all rule outcomes are in possible outcomes
        foreach (var rule in DecisionRules)
        {
            if (!PossibleOutcomes.Contains(rule.Outcome))
                errors.Add($"Rule outcome '{rule.Outcome}' is not in possible_outcomes");

            // Check that condition is not empty
            if (string.IsNullOrWhiteSpace(rule.Condition))
                errors.Add("Decision rule cannot have empty condition");
        }

        // Check that there's a default rule (catch-all with condition "true" or similar)
        var hasDefault = DecisionRules.Any(r => r.Condition.Equals("true", StringComparison.OrdinalIgnoreCase)
            || r.Condition.Equals("1 == 1", StringComparison.OrdinalIgnoreCase));

        if (!hasDefault)
            errors.Add("Warning: No default rule found. Add a rule with condition 'true' to catch all cases.");

        return errors;
    }

    /// <summary>
    /// Checks if an outcome value is valid for this framework.
    /// </summary>
    public bool IsValidOutcome(string outcome)
        => PossibleOutcomes.Contains(outcome);
}
