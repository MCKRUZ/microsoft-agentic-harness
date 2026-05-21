namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that consume LLM tokens.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Application.AI.Common.MediatRBehaviors.TokenBudgetBehavior{TRequest,TResponse}"/>
/// only applies to requests implementing this interface. Requests that do not implement
/// <see cref="IConsumesTokens"/> pass through the behavior without any budget check.
/// </para>
/// <para>
/// Implement this interface on any command or query that will invoke an LLM and therefore
/// contributes to the execution context's token budget.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public record InvokeAgentCommand(string Prompt) : IRequest&lt;AgentResponse&gt;, IConsumesTokens
/// {
///     public int EstimatedTokenCost =&gt; Prompt.Length / 4 + 500; // rough estimate
/// }
/// </code>
/// </example>
public interface IConsumesTokens
{
    /// <summary>
    /// Estimated token cost for this request. Used for pre-flight budget checks.
    /// </summary>
    /// <remarks>
    /// This is a pre-execution estimate. Actual token usage should be recorded via
    /// <see cref="Application.AI.Common.Interfaces.AI.ITokenBudgetTracker.RecordUsage"/>
    /// after the LLM call completes.
    /// </remarks>
    int EstimatedTokenCost { get; }
}
