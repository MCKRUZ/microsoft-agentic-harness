namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Runs the host's registered <see cref="IToolCallObserver"/> rules against a tool call that is
/// about to execute, and reduces their rulings to a single allow-or-block decision.
/// </summary>
/// <remarks>
/// <para>
/// Consulted at the same invocation chokepoint as the governor, the classification gate, and the
/// progress guard — and last among them, so an observer only sees calls the built-in gates have
/// already permitted. That ordering is what makes an observer unable to widen access: by the time
/// it runs, every question about whether the agent is <em>allowed</em> the tool has been settled.
/// </para>
/// <para>
/// <strong>Strictest ruling wins.</strong> Observers are consulted in registration order and the
/// first one that does not say "proceed" decides the call. A host cannot make a permissive observer
/// override a restrictive one by ordering, because no observer can return an outcome more
/// permissive than "no objection".
/// </para>
/// </remarks>
public interface IToolCallObserverChain
{
    /// <summary>
    /// Whether the host registered any observers. False on the default composition, letting the
    /// chokepoint skip the chain entirely rather than pay for an empty iteration on every call.
    /// </summary>
    bool HasObservers { get; }

    /// <summary>
    /// Puts a tool call to every registered observer and returns the resulting decision.
    /// </summary>
    /// <param name="toolName">The tool about to execute.</param>
    /// <param name="arguments">The arguments the model supplied.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An allow decision when every observer proceeded (or a human approved a call one of them
    /// escalated), otherwise a deny carrying the generic model-facing message.
    /// </returns>
    ValueTask<ToolInvocationDecision> EvaluateAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
}
