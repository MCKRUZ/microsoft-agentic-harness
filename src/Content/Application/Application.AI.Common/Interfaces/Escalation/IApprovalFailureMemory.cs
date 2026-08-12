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
    void RecordFailure(in ApprovalFailureKey key, string failureReason, Guid escalationId);

    /// <summary>Clears any recorded failure for this key.</summary>
    void Clear(in ApprovalFailureKey key);
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
public readonly record struct ApprovalFailureRecall(int PriorAttemptCount, string FailureReason, Guid EscalationId);
