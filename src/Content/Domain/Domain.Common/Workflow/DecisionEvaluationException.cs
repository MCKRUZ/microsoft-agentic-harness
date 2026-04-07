namespace Domain.Common.Workflow;

/// <summary>
/// Exception thrown when decision evaluation fails.
/// </summary>
public class DecisionEvaluationException : Exception
{
    public DecisionEvaluationException(string message) : base(message) { }
    public DecisionEvaluationException(string message, Exception innerException) : base(message, innerException) { }
}
