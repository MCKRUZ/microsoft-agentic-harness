namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// EF Core row recording that a call-once tool has already been called within one conversation.
/// The row's mere existence is the fact; there is nothing else to read or update once it is
/// written.
/// </summary>
/// <remarks>
/// <para>
/// Configured inline in <see cref="Infrastructure.AI.Persistence.GovernanceStateDbContext"/>,
/// matching <see cref="EscalationStateEntity"/> and <see cref="ChangeProposalEntity"/>. The
/// composite primary key on (<see cref="ConversationId"/>, <see cref="ToolName"/>) IS the
/// enforcement mechanism: a second insert for the same pair fails with a primary-key violation,
/// which <c>EfCoreToolCallLedger.TryClaimAsync</c> reads as "already claimed" rather than as an
/// unexpected failure. There is deliberately no read-then-write path — see that type's remarks
/// for why a check-then-insert would admit an entire parallel batch of the same tool call.
/// </para>
/// <para>
/// No seal, unlike the other two tables in this store. A forged or corrupted row here can only
/// make the system MORE restrictive — it records a call that never happened, so a real one is
/// wrongly refused — never less; there is no equivalent of laundering a forged approval for an
/// attacker to gain from. See <c>GovernanceDurableStateConfig.CallOnceEnforcementEnabled</c>.
/// </para>
/// </remarks>
public sealed class ToolCallLedgerEntity
{
    /// <summary>The conversation the call was made in. Part of the composite primary key.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The tool that was called. Part of the composite primary key.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>When the claim was recorded, as UTC ticks. Diagnostic only — not queried on.</summary>
    public long CalledAtTicks { get; set; }
}
