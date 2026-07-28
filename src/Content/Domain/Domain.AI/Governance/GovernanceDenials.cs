namespace Domain.AI.Governance;

/// <summary>
/// The canonical caller-facing text for a governance denial. Every gate that blocks an operation
/// returns this exact shape, so a denied caller cannot distinguish <em>why</em> it was denied from
/// the message alone.
/// </summary>
/// <remarks>
/// The deliberate vagueness is the point: rule ids, matched policy paths, capability internals, and
/// envelope contents stay in the structured log and the <see cref="GovernanceTrace"/>, never in text
/// relayed to a model or an HTTP caller. Centralising the wording here also stops the three gates
/// that emit it (the tool-invocation governor and the plan engine's tool and retrieval steps) from
/// drifting into three subtly different strings that leak which gate fired.
/// </remarks>
public static class GovernanceDenials
{
    /// <summary>
    /// The message returned in place of a result when the named operation is not permitted for the
    /// current caller, agent, or capability envelope.
    /// </summary>
    /// <param name="operationName">The tool or capability name that was denied.</param>
    /// <returns>The caller-facing denial text.</returns>
    public static string NotPermitted(string operationName) =>
        $"Error: tool '{operationName}' is not permitted in the current context.";
}
