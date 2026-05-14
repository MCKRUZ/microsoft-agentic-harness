using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Evaluates approver decisions against an escalation request to determine resolution.
/// Registered via keyed DI -- resolved by <see cref="ApprovalStrategyType"/>.
/// </summary>
/// <remarks>
/// <para>Three built-in strategies:</para>
/// <list type="bullet">
///   <item><c>AnyOf</c> -- first response wins (approve or deny)</item>
///   <item><c>AllOf</c> -- unanimous approval required, single denial resolves immediately</item>
///   <item><c>Quorum</c> -- N-of-M threshold, resolved when outcome is mathematically determined</item>
/// </list>
/// </remarks>
public interface IApprovalStrategy
{
    /// <summary>
    /// Evaluates collected decisions against the request's approval requirements.
    /// </summary>
    /// <param name="request">The escalation request containing approver list and threshold config.</param>
    /// <param name="decisions">All decisions collected so far.</param>
    /// <returns>Evaluation result indicating whether the escalation is resolved and the verdict.</returns>
    ApprovalEvaluation EvaluateDecision(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions);

    /// <summary>
    /// The strategy type this implementation handles. Used as the keyed DI key.
    /// </summary>
    ApprovalStrategyType StrategyType { get; }
}
