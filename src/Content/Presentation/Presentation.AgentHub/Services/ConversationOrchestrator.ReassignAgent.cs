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
        // for no reason. This also settles the no-op check (a client resending the same agentName)
        // without ever touching the lease or the cache for it, using the same case-insensitive
        // comparison other agent-identifier checks in this codebase use.
        var current = await _conversationStore.GetAsync(conversationId, callerId, ct);
        if (current is null)
            return null;

        if (string.Equals(current.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately not re-verified under the lease: doing so would mean every no-op
            // reassignment pays the same lease-acquisition cost this fast path exists to avoid. The
            // accepted gap is narrow and matches the pre-lease-read tradeoff already documented on
            // WithTurnLeaseAsync -- if a different reassignment commits between this read and this
            // return, the response can report a current-agent value that is no longer the true
            // latest one. It never causes a wrong WRITE (this path never writes), only a possibly
            // stale READ in the reply to a coincidental race between two reassignment calls.
            return current;
        }

        // Held for the write AND the eviction, not just the write, and through the SAME
        // WithTurnLeaseAsync every turn-producing method uses -- not a second, hand-rolled copy of
        // lease handling -- so a lease lost mid-reassignment is linked into the token driving both
        // calls exactly like every other lease-holding operation here, instead of silently letting
        // the write and eviction complete after another host has taken over. This is what makes the
        // write and the cache eviction atomic with respect to ordinary turn dispatch:
        // DispatchTurnAsync re-reads the record fresh from inside its OWN held lease before deciding
        // which agent to fetch or build, report telemetry for, or fold health/usage into (see its
        // dispatchAgentName), so a turn that raced this call for the lease and lost sees the
        // reassignment's result in full once it finally acquires the lease itself -- it can no
        // longer dispatch against, report on, or re-cache the agent this call just reassigned away
        // from using a value it captured before either lease was ever contested.
        return await WithTurnLeaseAsync(conversationId, async leaseCt =>
        {
            var updated = await _conversationStore.ReassignAgentAsync(conversationId, callerId, agentName, leaseCt);
            if (updated is null)
                return null;

            _agentCache.Evict(conversationId);
            return updated;
        }, ct);
    }
}
