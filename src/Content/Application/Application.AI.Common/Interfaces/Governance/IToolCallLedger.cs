namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Durably records which call-once tools have already been called in which conversations, so a
/// second call can be refused regardless of what the model remembers, what turn it is, what run
/// it is, or which host answers the call.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One method, and it is both the check and the record.</strong> There is deliberately
/// no separate "has this been called" read — a read-then-write here would admit an entire
/// parallel batch of the same tool call within one assistant message, since every call in the
/// batch would read "not yet claimed" before any of them wrote. This is the same atomicity
/// requirement <c>IProgressEvaluator</c>'s own docs describe for the loop guard, for the same
/// reason: deciding and recording must be one operation.
/// </para>
/// <para>
/// Selected by DI based on <c>GovernanceDurableStateConfig.CallOnceEnforcementEnabled</c> — the
/// caller (the admission gate) never checks that toggle itself, matching how
/// <c>IEscalationStateStore</c> selects between a durable and an in-memory implementation.
/// </para>
/// </remarks>
public interface IToolCallLedger
{
    /// <summary>
    /// Attempts to claim <paramref name="toolName"/> for <paramref name="conversationId"/>.
    /// </summary>
    /// <param name="conversationId">The conversation the call is being made in.</param>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this is the first successful claim — the call may proceed.
    /// <see langword="false"/> when the pair was already claimed — the call must be refused.
    /// </returns>
    Task<bool> TryClaimAsync(string conversationId, string toolName, CancellationToken ct);
}
