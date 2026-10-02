using Application.AI.Common.Models.Conversations;

namespace Presentation.AgentHub.Services;

public sealed partial class ConversationOrchestrator
{
    /// <inheritdoc />
    public async Task<ConversationRecord?> ReassignAgentAsync(
        string conversationId, string callerId, string agentName, CancellationToken ct)
    {
        // Read BEFORE acquiring the lease, not after -- IConversationTurnLease's own contract
        // requires it: authorization happens via IConversationStore.GetAsync before ever touching
        // the lease, because the durable implementation throws InvalidOperationException for a
        // conversation it cannot find, and an unauthorized caller must never hold the lease even
        // briefly -- doing so would stall the real owner's concurrent turn on this same conversation
        // for no reason. Only the owner and the bound agent are needed to decide that, so this reads
        // the header, not the transcript.
        var preLeaseAgentName = await _conversationStore.GetAgentNameAsync(conversationId, callerId, ct);
        if (preLeaseAgentName is null)
            return null;

        // Held for the no-op determination, the write, AND the eviction -- not just the write -- and
        // through the SAME WithTurnLeaseAsync every turn-producing method uses, not a second,
        // hand-rolled copy of lease handling, so a lease lost mid-reassignment is linked into the
        // token driving all three exactly like every other lease-holding operation here. This is what
        // makes the write and the cache eviction atomic with respect to ordinary turn dispatch:
        // DispatchTurnAsync re-reads the record fresh from inside its OWN held lease before deciding
        // which agent to fetch or build, report telemetry for, or fold health/usage into (see its
        // dispatchAgentName), so a turn that raced this call for the lease and lost sees the
        // reassignment's result in full once it finally acquires the lease itself -- it can no
        // longer dispatch against, report on, or re-cache the agent this call just reassigned away
        // from using a value it captured before either lease was ever contested.
        return await WithTurnLeaseAsync(conversationId, async leaseCt =>
        {
            // Re-read fresh, under the lease, rather than reusing the pre-lease `preLeaseAgentName` above --
            // review caught a real race in an earlier version of this fix that compared the
            // REQUESTED name against a pre-lease snapshot: a second, concurrent reassignment (or an
            // interleaving turn dispatch that rebuilt and re-cached the agent for a DIFFERENT
            // in-flight reassignment) could land between that snapshot and this write, making the
            // pre-lease snapshot stale by the time the no-op decision actually matters. Determining
            // "did the agent actually change" from state read inside the same lease the write and
            // eviction happen under closes that window completely, the same way dispatchAgentName
            // closes it for ordinary turns.
            var currentUnderLease = await _conversationStore.GetAgentNameAsync(conversationId, callerId, leaseCt);
            if (currentUnderLease is null)
                return null;

            var isNoOp = string.Equals(currentUnderLease, agentName, StringComparison.OrdinalIgnoreCase);

            // The write always happens, even on a no-op -- every version of this call before this fix
            // went straight to IConversationStore.ReassignAgentAsync with no equality check at all,
            // and that write's UpdatedAt bump is what keeps this conversation sorted correctly in
            // EfCoreConversationStore.ListAsync's OrderByDescending(UpdatedAt). Skipping the write on
            // a same-name request would silently stop a coincidental same-agent PATCH from touching
            // that ordering, changing behavior nothing asked to change. What's actually skipped on a
            // genuine no-op is only the cache eviction below -- there is no reason to discard a live,
            // correctly-configured cached agent when nothing about it changed.
            var updated = await _conversationStore.ReassignAgentAsync(conversationId, callerId, agentName, leaseCt);
            if (updated is null)
                return null;

            if (!isNoOp)
                _agentCache.Evict(conversationId);

            return updated;
        }, ct);
    }
}
