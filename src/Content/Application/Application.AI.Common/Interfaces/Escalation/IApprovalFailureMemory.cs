using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Bounded, conversation-scoped memory of failed approved attempts, so a corrected retry can be
/// attributed to its predecessor instead of presented as a fresh ask. Every operation is a
/// dictionary hit — deliberately synchronous, unlike the durable escalation stores, because this
/// has no durable sibling to justify an async signature.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key is (conversation, agent, tool) — arguments are deliberately excluded.</strong>
/// A corrected retry has different arguments by definition, so keying on arguments would
/// guarantee a miss on the exact case this exists to catch. Keying on conversation and tool alone
/// risks cross-labeling when a supervisor and a delegated sub-agent both call the same tool in
/// one conversation, so the agent id is included too. The residual over-breadth — two genuinely
/// unrelated calls to the same tool by the same agent in one conversation matching — is accepted
/// on purpose: this field is advisory context on an approval card, not a gate, and a false
/// positive costs an approver a moment's confusion while a false negative reproduces the exact
/// rubber-stamp failure this feature exists to prevent.
/// </para>
/// <para>
/// Cleared only on an explicit human denial, never on timeout or escalation — a timeout means
/// nobody looked, and erasing the context the next approver needs would invert the feature.
/// </para>
/// </remarks>
public interface IApprovalFailureMemory
{
    /// <summary>Returns the recalled prior-failure state for this key, or null if none is recorded.</summary>
    ApprovalFailureRecall? TryRecall(in ApprovalFailureKey key);

    /// <summary>Records a failed approved attempt against this key.</summary>
    /// <param name="failureReasonSubstitution">See <see cref="ApprovalFailureRecall.Substitution"/>.</param>
    void RecordFailure(
        in ApprovalFailureKey key, string failureReason, FailureTextSubstitution failureReasonSubstitution,
        Guid escalationId);

    /// <summary>
    /// Clears any recorded failure for this key — and, as a side effect, any recorded revision
    /// state for the same key too (see <see cref="ClearRevision"/>'s remarks). This removes the
    /// whole per-key entry, not just its failure half, so a future caller must not assume it can
    /// clear failure state alone without disturbing a still-live revision round.
    /// </summary>
    /// <remarks>
    /// Safe on both of today's production callers, for different reasons: an explicit human denial
    /// means any revision conversation for the key is genuinely over too, so the wider clear is
    /// exactly right; a successfully executed approved call runs this only after the router's own
    /// approval path already called <see cref="ClearRevision"/> at decision time, so the side effect
    /// here is redundant, not destructive. A new caller with neither property must call
    /// <see cref="ClearRevision"/> instead if it needs to leave failure state untouched.
    /// </remarks>
    void Clear(in ApprovalFailureKey key);

    /// <summary>
    /// Returns the recalled prior-revision state for this key, or null if none is recorded.
    /// </summary>
    /// <remarks>
    /// A wholly independent piece of state from <see cref="TryRecall"/>'s failed-attempt tracking,
    /// sharing this same bounded cache rather than a second one with its own cap — see
    /// <see cref="RecordRevision"/> for why the two must never share a clear rule.
    /// </remarks>
    ApprovalRevisionRecall? TryRecallRevision(in ApprovalFailureKey key);

    /// <summary>
    /// Records that this key's escalation resolved with a Revise verdict, so the next attempt at
    /// the same action can carry the round number forward and show the reviewer's instructions.
    /// </summary>
    void RecordRevision(in ApprovalFailureKey key, int revisionRound, string instructions, Guid escalationId);

    /// <summary>
    /// Clears any recorded revision state for this key, independently of <see cref="Clear"/>.
    /// </summary>
    /// <remarks>
    /// Callers must invoke this whenever an escalation for the key resolves Approved or Denied —
    /// the revise conversation is over at that decision, not at whatever the approved call later
    /// does. Never call this from a runtime execution outcome: a revision that led to an approved
    /// call which later fails at runtime is <see cref="RecordFailure"/>'s concern, not this one's,
    /// and clearing revision state on that failure would let a still-live round's instructions and
    /// round count silently vanish before the reviewer's cap is reached.
    /// </remarks>
    void ClearRevision(in ApprovalFailureKey key);
}

/// <summary>
/// The failure-memory key for one action: the conversation it happened in, the agent that
/// attempted it, and the tool it called. Compared ordinally throughout — this is a machine key,
/// not a human identity, so it does not use <see cref="Domain.AI.Escalation.ApproverNames"/>'
/// case-insensitive comparer.
/// </summary>
public readonly record struct ApprovalFailureKey(string ConversationId, string AgentId, string ToolName)
{
    /// <summary>
    /// Builds a key, or null when there is no conversation to scope it to. The two production call
    /// sites (<c>EscalationToolApprovalRouter</c> and <c>ToolInvocationGovernor.BuildApprovedCall</c>)
    /// both build this key from the same ambient execution context and shared this exact guard
    /// independently before it was extracted here — sharing it means the "missing conversation
    /// identity → no key, never a sentinel" rule cannot drift between them.
    /// </summary>
    public static ApprovalFailureKey? TryCreate(string? conversationId, string agentId, string toolName) =>
        string.IsNullOrWhiteSpace(conversationId) ? null : new ApprovalFailureKey(conversationId, agentId, toolName);
}

/// <summary>A recalled prior failure: how many times it has failed, why, and which escalation last approved it.</summary>
/// <param name="Substitution">
/// Why <see cref="FailureReason"/> is a substitute rather than the tool's own message (#472) — see
/// <see cref="Domain.AI.Escalation.FailureTextSubstitution"/>.
/// </param>
public readonly record struct ApprovalFailureRecall(
    int PriorAttemptCount, string FailureReason, FailureTextSubstitution Substitution, Guid EscalationId);

/// <summary>
/// A recalled prior revision: which round the next attempt continues, what the reviewer asked for
/// last time, and the escalation that asked for it. Independent of <see cref="ApprovalFailureRecall"/>
/// — see <see cref="IApprovalFailureMemory.ClearRevision"/> for why the two must never share a
/// clear rule despite sharing a cache.
/// </summary>
public readonly record struct ApprovalRevisionRecall(int RevisionRound, string Instructions, Guid EscalationId);
