using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Services.Escalation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Escalation;

/// <summary>
/// Tests for <see cref="InProcessApprovalFailureMemory"/> — the bounded, conversation-scoped
/// record of failed approved attempts that #325 retry attribution reads from and writes to.
/// </summary>
public sealed class InProcessApprovalFailureMemoryTests
{
    private static InProcessApprovalFailureMemory Create() =>
        new(TimeProvider.System, NullLogger<InProcessApprovalFailureMemory>.Instance);

    private static ApprovalFailureKey Key(string conversationId = "conv-1", string agentId = "agent-1", string toolName = "file_system") =>
        new(conversationId, agentId, toolName);

    [Fact]
    public void TryRecall_NothingRecorded_ReturnsNull()
    {
        var memory = Create();

        var recall = memory.TryRecall(Key());

        Assert.Null(recall);
    }

    [Fact]
    public void RecordFailure_ThenTryRecall_ReturnsWhatWasRecorded()
    {
        var memory = Create();
        var key = Key();
        var escalationId = Guid.NewGuid();

        memory.RecordFailure(key, "permission denied", escalationId);
        var recall = memory.TryRecall(key);

        Assert.NotNull(recall);
        Assert.Equal(1, recall!.Value.PriorAttemptCount);
        Assert.Equal("permission denied", recall.Value.FailureReason);
        Assert.Equal(escalationId, recall.Value.EscalationId);
    }

    [Fact]
    public void RecordFailure_TwiceForSameKey_IncrementsAttemptCountAndKeepsLatestReason()
    {
        var memory = Create();
        var key = Key();

        memory.RecordFailure(key, "first failure", Guid.NewGuid());
        var secondEscalationId = Guid.NewGuid();
        memory.RecordFailure(key, "second failure", secondEscalationId);

        var recall = memory.TryRecall(key);

        Assert.NotNull(recall);
        Assert.Equal(2, recall!.Value.PriorAttemptCount);
        Assert.Equal("second failure", recall.Value.FailureReason);
        Assert.Equal(secondEscalationId, recall.Value.EscalationId);
    }

    [Fact]
    public void RecordFailure_BlankFailureReason_Throws()
    {
        var memory = Create();

        Assert.ThrowsAny<ArgumentException>(() => memory.RecordFailure(Key(), "", Guid.NewGuid()));
    }

    [Fact]
    public void Clear_RemovesTheRecordedEntry()
    {
        var memory = Create();
        var key = Key();
        memory.RecordFailure(key, "boom", Guid.NewGuid());

        memory.Clear(key);

        Assert.Null(memory.TryRecall(key));
    }

    [Fact]
    public void Clear_UnknownKey_MutationControl_DoesNotThrow()
    {
        var memory = Create();

        var exception = Record.Exception(() => memory.Clear(Key("never-recorded")));

        Assert.Null(exception);
    }

    [Fact]
    public void DifferentConversationIds_AreIsolated()
    {
        // The key is (conversation, agent, tool) precisely so one conversation's retry history can
        // never label another's approval card.
        var memory = Create();
        memory.RecordFailure(Key(conversationId: "conv-a"), "boom", Guid.NewGuid());

        var recall = memory.TryRecall(Key(conversationId: "conv-b"));

        Assert.Null(recall);
    }

    [Fact]
    public void DifferentAgentIds_InSameConversation_AreIsolated()
    {
        // A supervisor and a delegated sub-agent calling the same tool in one conversation must not
        // cross-label each other's retry history.
        var memory = Create();
        memory.RecordFailure(Key(agentId: "supervisor"), "boom", Guid.NewGuid());

        var recall = memory.TryRecall(Key(agentId: "sub-agent"));

        Assert.Null(recall);
    }

    [Fact]
    public void EvictsLeastRecentlyUsed_WhenOverCapacity()
    {
        var memory = Create();

        for (var i = 0; i <= InProcessApprovalFailureMemory.MaxTrackedActions; i++)
            memory.RecordFailure(Key(conversationId: $"conv-{i}"), "boom", Guid.NewGuid());

        // The most recently recorded entry must survive — the cap is respected, not exceeded.
        var survivor = Key(conversationId: $"conv-{InProcessApprovalFailureMemory.MaxTrackedActions}");
        Assert.NotNull(memory.TryRecall(survivor));
    }

    [Fact]
    public void EvictedEntry_TryRecallReturnsNull_WhichIsTheDocumentedTradeOff()
    {
        // Recorded first and never touched again, so it is the least-recently-used entry once the
        // cap is crossed. Pinned so the eviction trade-off (a benign miss, not a crash) is visible
        // — EscalationRequestInvariants explicitly accepts the resulting "attempt > 1 with no prior
        // failure reason" shape as valid rather than treating it as corruption.
        var memory = Create();
        var evicted = Key(conversationId: "first");
        memory.RecordFailure(evicted, "boom", Guid.NewGuid());

        for (var i = 0; i <= InProcessApprovalFailureMemory.MaxTrackedActions; i++)
            memory.RecordFailure(Key(conversationId: $"conv-{i}"), "boom", Guid.NewGuid());

        Assert.Null(memory.TryRecall(evicted));
    }

    // ===== #321 revision tracking: an independent field group on the same entry =====

    [Fact]
    public void TryRecallRevision_NothingRecorded_ReturnsNull()
    {
        var memory = Create();

        Assert.Null(memory.TryRecallRevision(Key()));
    }

    [Fact]
    public void RecordRevision_ThenTryRecallRevision_ReturnsWhatWasRecorded()
    {
        var memory = Create();
        var key = Key();
        var escalationId = Guid.NewGuid();

        memory.RecordRevision(key, revisionRound: 2, "use the read-only endpoint instead", escalationId);
        var recall = memory.TryRecallRevision(key);

        Assert.NotNull(recall);
        Assert.Equal(2, recall!.Value.RevisionRound);
        Assert.Equal("use the read-only endpoint instead", recall.Value.Instructions);
        Assert.Equal(escalationId, recall.Value.EscalationId);
    }

    [Fact]
    public void RecordRevision_BlankInstructions_Throws()
    {
        var memory = Create();

        Assert.ThrowsAny<ArgumentException>(() => memory.RecordRevision(Key(), 2, "  ", Guid.NewGuid()));
    }

    [Fact]
    public void ClearRevision_RemovesTheRecordedRevisionState()
    {
        var memory = Create();
        var key = Key();
        memory.RecordRevision(key, 2, "revise this", Guid.NewGuid());

        memory.ClearRevision(key);

        Assert.Null(memory.TryRecallRevision(key));
    }

    [Fact]
    public void ClearRevision_UnknownKey_MutationControl_DoesNotThrow()
    {
        var memory = Create();

        var exception = Record.Exception(() => memory.ClearRevision(Key("never-recorded")));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordFailure_DoesNotAffectRevisionState()
    {
        // Coexistence, order 1: a failed approved attempt is recorded on a key that already has
        // live revision state (the retry that followed an earlier revise was itself approved, ran,
        // and failed). The failure write must not clobber the still-relevant revision history.
        var memory = Create();
        var key = Key();
        memory.RecordRevision(key, 2, "use the read-only endpoint instead", Guid.NewGuid());

        memory.RecordFailure(key, "timed out", Guid.NewGuid());

        var revision = memory.TryRecallRevision(key);
        Assert.NotNull(revision);
        Assert.Equal(2, revision!.Value.RevisionRound);
        Assert.Equal("use the read-only endpoint instead", revision.Value.Instructions);
    }

    [Fact]
    public void RecordRevision_ThenTryRecall_ReturnsNullRatherThanAFabricatedZeroAttemptFailure()
    {
        // Regression test. RecordRevision GetOrAdd's the same Entry TryRecall reads — without a
        // zero-attempt-count guard, this makes TryRecall fabricate ApprovalFailureRecall(0, "", ...)
        // for a key whose failure half was never touched, which BuildRequest turns into
        // AttemptNumber=1 with a non-null (empty) PriorFailureReason — a shape
        // EscalationRequestInvariants rejects outright, fail-closing the very next approval
        // attempt after every successful revise.
        var memory = Create();
        var key = Key();

        memory.RecordRevision(key, 2, "use the read-only endpoint instead", Guid.NewGuid());

        Assert.Null(memory.TryRecall(key));
    }

    [Fact]
    public void RecordRevision_DoesNotAffectFailureState()
    {
        // Coexistence, order 2: the reverse sequence. A prior failed approved attempt is on record
        // when a fresh escalation for the same key gets revised — RecordRevision must not touch
        // the failure fields RecordFailure owns.
        var memory = Create();
        var key = Key();
        memory.RecordFailure(key, "permission denied", Guid.NewGuid());

        memory.RecordRevision(key, 2, "narrow the scope", Guid.NewGuid());

        var failure = memory.TryRecall(key);
        Assert.NotNull(failure);
        Assert.Equal(1, failure!.Value.PriorAttemptCount);
        Assert.Equal("permission denied", failure.Value.FailureReason);
    }

    [Fact]
    public void ClearRevision_LeavesFailureStateIntact()
    {
        var memory = Create();
        var key = Key();
        memory.RecordFailure(key, "permission denied", Guid.NewGuid());
        memory.RecordRevision(key, 2, "narrow the scope", Guid.NewGuid());

        memory.ClearRevision(key);

        Assert.Null(memory.TryRecallRevision(key));
        var failure = memory.TryRecall(key);
        Assert.NotNull(failure);
        Assert.Equal("permission denied", failure!.Value.FailureReason);
    }

    [Fact]
    public void Clear_RemovesRevisionStateToo()
    {
        // Denied removes the whole entry via Clear() rather than a separate ClearRevision call —
        // there is nothing left to revise once a human has said no. Pinned so a future change to
        // either method can't silently leave stale revision state behind after an explicit denial.
        var memory = Create();
        var key = Key();
        memory.RecordRevision(key, 2, "narrow the scope", Guid.NewGuid());

        memory.Clear(key);

        Assert.Null(memory.TryRecallRevision(key));
    }
}
