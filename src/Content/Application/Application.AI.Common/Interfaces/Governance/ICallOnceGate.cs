namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Admission stage 6: for a tool declared <c>CallOncePerConversation</c>, durably refuses a
/// second call within the same conversation — regardless of what the model remembers, what turn
/// it is, what run it is, or which host answers the call.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Last, after the loop guard, and for the same "checking is claiming" reason.</strong>
/// A call this gate lets through has already cleared every access question the earlier five
/// stages ask; this stage answers a different kind of question entirely — not "may this call
/// happen", but "has this call already happened" — and asking it any earlier would waste a
/// durable write on a call that was going to be refused anyway.
/// </para>
/// <para>
/// <strong>No in-process state, unlike the loop guard.</strong> Every check is a durable
/// read/claim through <see cref="IToolCallLedger"/>; a tool that was never declared call-once
/// short-circuits to an allow before the ledger is touched at all, so the cost lands only on
/// tools that opt in. There is nothing for <see cref="IToolCallAdmissionPipeline.Reset"/> to
/// clear here — durability is the point.
/// </para>
/// </remarks>
public interface ICallOnceGate
{
    /// <summary>
    /// Decides whether <paramref name="toolName"/> may be called in the current conversation.
    /// </summary>
    /// <param name="toolName">The tool being admitted.</param>
    /// <param name="cancellationToken">Cancels the durable check. Cancellation propagates rather than becoming a verdict.</param>
    /// <returns>
    /// An allow when the tool was not declared call-once, when the current execution carries no
    /// conversation id (nothing durable to key the claim on — see remarks on
    /// <c>CallOnceGate</c> for why this fails open rather than closed), or when this is the
    /// first call; otherwise a denial carrying caller-facing text specific enough for the model
    /// to act on, mirroring the loop guard's halt message rather than the generic access-control
    /// denial.
    /// </returns>
    ValueTask<ToolInvocationDecision> EvaluateAsync(string toolName, CancellationToken cancellationToken);
}
