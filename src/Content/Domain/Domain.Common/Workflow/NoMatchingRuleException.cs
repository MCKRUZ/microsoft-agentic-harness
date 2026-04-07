namespace Domain.Common.Workflow;

/// <summary>
/// Exception thrown when no decision rule matches.
/// </summary>
public class NoMatchingRuleException : DecisionEvaluationException
{
    public NoMatchingRuleException(string message) : base(message) { }
}
