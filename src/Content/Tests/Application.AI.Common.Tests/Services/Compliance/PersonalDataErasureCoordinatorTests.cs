using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Application.Common.Interfaces.Compliance;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// The coordinator's order and failure rules (#900): conversation ids are read before anything is deleted, the
/// conversations go only after everything reached through them, and one failing store neither stops the rest nor lets
/// the receipt claim more than happened.
/// </summary>
public sealed class PersonalDataErasureCoordinatorTests
{
    private const string Owner = "Owner-1";
    private readonly Mock<IErasureOrchestrator> _graph = new();
    private readonly Mock<IConversationStore> _conversations = new();
    private readonly List<string> _calls = [];
    private readonly AmbientRequestScope _ambientScope = new();

    public PersonalDataErasureCoordinatorTests()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Full));
        // Listed once before anything is deleted, and once afterwards to find conversations started meanwhile.
        _conversations.SetupSequence(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation("c-1"), Conversation("c-2")])
            .ReturnsAsync([]);
    }

    private static ErasureReceipt GraphReceipt(ErasureCompleteness completeness, string? reason = null) => new()
    {
        RequestId = "r",
        ScopeId = Owner,
        RequestedAt = DateTimeOffset.UnixEpoch,
        CompletedAt = DateTimeOffset.UnixEpoch,
        NodesDeleted = 1,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 0,
        Completeness = completeness,
        CompletenessReason = reason,
    };

    private static ConversationRecord Conversation(string id) =>
        new(id, "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

    private IPersonalDataEraser Eraser(string name, ErasureStage stage, int removed = 1, Exception? failure = null) =>
        EraserMock(name, stage, removed, failure).Object;

    private Mock<IPersonalDataEraser> EraserMock(string name, ErasureStage stage, int removed = 1, Exception? failure = null)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Returns<ErasureSubject, CancellationToken>((_, _) =>
            {
                _calls.Add(name);
                return failure is null ? Task.FromResult(removed) : Task.FromException<int>(failure);
            });
        return eraser;
    }

    private IPersonalDataEraser ScrubbingEraser(
        string name, ErasureStage stage, int removed = 1, Exception? scrubFailure = null)
    {
        var eraser = EraserMock(name, stage, removed);
        eraser.As<IPersonalDataScrubber>()
            .Setup(s => s.ScrubAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                _calls.Add($"scrub {name}");
                return scrubFailure is null ? Task.CompletedTask : Task.FromException(scrubFailure);
            });
        return eraser.Object;
    }

    private PersonalDataErasureCoordinator Create(params IPersonalDataEraser?[] erasers) =>
        new(_graph.Object, erasers!, _retained, _ambientScope, TimeProvider.System,
            NullLogger<PersonalDataErasureCoordinator>.Instance, _conversations.Object);

    private readonly List<RetainedPersonalData> _retained = [];

    [PersonalDataStore("Test log: unkeyed, kept forever.", PersonalDataCategories.ConversationContent,
        PersonalDataKey.Unkeyed, RetentionBasis.Unbounded, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class ForeverLog;

    [PersonalDataStore("Test cache: unkeyed, in memory only.", PersonalDataCategories.ConversationContent,
        PersonalDataKey.Unkeyed, RetentionBasis.Volatile, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class ExpiringCache;

    [PersonalDataStore("Test state: unkeyed, retention configured.", PersonalDataCategories.ToolInputsAndOutputs,
        PersonalDataKey.Unkeyed, RetentionBasis.Configured, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class ConfiguredRetentionStore;

    [PersonalDataStore("Test audit trail: kept on purpose.", PersonalDataCategories.Identifiers | PersonalDataCategories.ToolInputsAndOutputs,
        PersonalDataKey.Unkeyed, RetentionBasis.KeptByDesign, ErasureSupport.Exempt, ExportSupport.NotSupported)]
    private sealed class AuditTrail;

    [Fact]
    public async Task StoreWithConfiguredRetention_IsKeptIndefinitelyAndMakesTheReceiptPartial()
    {
        // A configured retention period can be switched off, and such stores keep open items regardless, so "ages out"
        // would overclaim.
        _retained.Add(new RetainedPersonalData("escalation state", new ConfiguredRetentionStore()));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().ContainSingle(s => s.Store == "escalation state")
            .Which.Status.Should().Be(StoreErasureStatus.RetainedIndefinitely);
    }

    [PersonalDataStore("Test cache: unkeyed, every record expires.", PersonalDataCategories.ConversationContent,
        PersonalDataKey.Unkeyed, RetentionBasis.Configured, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class PeriodStore(TimeSpan? period) : IStoreRetentionPeriod
    {
        public TimeSpan? CurrentRetentionPeriod { get; } = period;
    }

    [Fact]
    public async Task StoreWithARunningRetentionPeriod_IsListedWithThePeriod_AndTheReceiptStaysFull()
    {
        // #901: a store whose live configuration deletes every record within a period ages out; it says how long.
        _retained.Add(new RetainedPersonalData("chat response cache", new PeriodStore(TimeSpan.FromDays(90))));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        var line = receipt.Stores.Should().ContainSingle(s => s.Store == "chat response cache").Subject;
        line.Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry);
        line.Reason.Should().EndWith("deleted within 90 days of being written");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task StoreWhoseRetentionIsSwitchedOff_IsKeptIndefinitely(int? days)
    {
        _retained.Add(new RetainedPersonalData("delegation log", new PeriodStore(days is { } d ? TimeSpan.FromDays(d) : null)));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Single(s => s.Store == "delegation log").Status.Should().Be(StoreErasureStatus.RetainedIndefinitely);
    }

    [Theory]
    [InlineData(1, "1 hour")]
    [InlineData(48, "2 days")]
    [InlineData(36, "36 hours")]
    public void ThePeriod_IsWrittenInPlainWords(int hours, string expected)
    {
        new RetainedPersonalData("cache", new PeriodStore(TimeSpan.FromHours(hours))).ToOutcome()!
            .Reason.Should().EndWith($"within {expected} of being written");
    }

    [Fact]
    public async Task AuditTrail_IsListedWithTheKindsOfDataItHolds()
    {
        // Kept on purpose, it does not make the receipt Partial, so its line must not imply it holds nothing.
        _retained.Add(new RetainedPersonalData("escalation audit trail", new AuditTrail()));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle(s => s.Store == "escalation audit trail")
            .Which.Reason.Should().Contain("identifiers").And.Contain("tool inputs and outputs");
    }

    [Fact]
    public async Task DocumentsOthersLoaded_AreListedWithTheReason_AndTheReceiptStaysFull()
    {
        // #901: the person's own uploads are erased by owner; documents others loaded are the organisation's business
        // content. The person is told, without the receipt claiming an erasure failed.
        _retained.Add(RetainedPersonalData.BusinessContent("documents other people loaded", ageLimit: null));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        var line = receipt.Stores.Should().ContainSingle(s => s.Store == "documents other people loaded").Subject;
        line.Status.Should().Be(StoreErasureStatus.RetainedAsBusinessContent);
        line.Reason.Should().Be(RetainedPersonalData.BusinessContentReason);
    }

    [Fact]
    public void DocumentsOthersLoaded_WithTheAgeLimitRunning_SayHowLong() =>
        RetainedPersonalData.BusinessContent("documents other people loaded", TimeSpan.FromDays(1095)).ToOutcome()!
            .Reason.Should().Be($"{RetainedPersonalData.BusinessContentReason}, or until 1095 days after they were last loaded");

    [PersonalDataStore("Test store keyed to the owner that cannot erase.", PersonalDataCategories.Identifiers,
        PersonalDataKey.Owner, RetentionBasis.Unbounded, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class OwnerKeyedStore;

    [Fact]
    public async Task StoreKeptForever_IsListedAndMakesTheReceiptPartial()
    {
        // Choice A (#900): "complete" while a log keeps transcripts with no end date is the overclaim #900 was filed for.
        _retained.Add(new RetainedPersonalData("application logs", new ForeverLog()));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().ContainSingle(s => s.Store == "application logs")
            .Which.Status.Should().Be(StoreErasureStatus.RetainedIndefinitely);
        receipt.CompletenessReason.Should().Contain("application logs");
    }

    [Fact]
    public async Task StoresThatAgeOutOrAreKeptByObligation_AreListedAndTheReceiptStaysFull()
    {
        _retained.Add(new RetainedPersonalData("response cache", new ExpiringCache()));
        _retained.Add(new RetainedPersonalData("governance audit trail", new AuditTrail()));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        receipt.Stores.Should().Contain(s => s.Store == "response cache" && s.Status == StoreErasureStatus.RetainedUntilExpiry);
        receipt.Stores.Should().Contain(s => s.Store == "governance audit trail" && s.Status == StoreErasureStatus.RetainedByObligation);
    }

    [Fact]
    public async Task StoreSwitchedOffOrKeyedToTheOwner_IsNotListedAsRetained()
    {
        // Switched off: the host resolves a null object (here, nothing). Keyed to the owner: it appears as NotSupported
        // through its erasure registration instead, so listing it here would count it twice.
        _retained.Add(new RetainedPersonalData("disabled store", null));
        _retained.Add(new RetainedPersonalData("owner-keyed store", new OwnerKeyedStore()));
        _retained.Add(new RetainedPersonalData("plain object", new object()));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().NotContain(s => s.Store == "disabled store" || s.Store == "owner-keyed store" || s.Store == "plain object");
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task SameStoreRegisteredTwice_IsListedOnce()
    {
        var log = new ForeverLog();
        _retained.Add(new RetainedPersonalData("application logs", log));
        _retained.Add(new RetainedPersonalData("application logs", log));

        var receipt = await Create().EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Count(s => s.Store == "application logs").Should().Be(1);
    }

    [Fact]
    public async Task CallerHasARequestScope_EveryStoreRunsSystemScopedAndTheCallersScopeComesBack()
    {
        // Scope-filtering stores answer by the ambient caller. An operator's scope would hide the subject's data from them
        // while the receipt still read Full, so erasure must run with no ambient scope at all, as background work does.
        var callerScope = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? seenByGraph = callerScope, seenByStore = callerScope;
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => seenByGraph = _ambientScope.Current)
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Full));
        var store = new Mock<IPersonalDataEraser>();
        store.SetupGet(e => e.ErasureStoreName).Returns("spills");
        store.SetupGet(e => e.ErasureStage).Returns(ErasureStage.BeforeConversations);
        store.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Callback(() => seenByStore = _ambientScope.Current)
            .ReturnsAsync(1);

        using (_ambientScope.BeginScope(callerScope))
        {
            await Create(store.Object).EraseOwnerAsync(Owner, CancellationToken.None);
            _ambientScope.Current.Should().BeSameAs(callerScope, "the caller's scope is restored afterwards");
        }

        seenByGraph.Should().BeNull();
        seenByStore.Should().BeNull();
    }

    [Fact]
    public async Task EveryStoreSucceeds_ReceiptIsFullAndListsEachStore()
    {
        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, removed: 3),
                Eraser("conversations", ErasureStage.Conversations, removed: 2),
                Eraser("ledger", ErasureStage.AfterConversations, removed: 4))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        receipt.CompletenessReason.Should().BeNull();
        receipt.Stores.Should().Equal(
            StoreErasureOutcome.Erased("spills", 3),
            StoreErasureOutcome.Erased("conversations", 2),
            StoreErasureOutcome.Erased("ledger", 4));
        receipt.NodesDeleted.Should().Be(1, "the graph's own counts are kept on the combined receipt");
    }

    private static readonly PlanRunTraces Runs = new(["plan-1:step-a"], ["job-1"], ["planrun:plan-1"]);

    private Mock<IPlanRunTraceSource> _planRunSource = new();

    private PersonalDataErasureCoordinator CreateWithPlanRuns(PlanRunTraces runs, params IPersonalDataEraser?[] erasers)
    {
        // Listed once before anything is deleted, and once afterwards to find runs made meanwhile.
        _planRunSource = new Mock<IPlanRunTraceSource>();
        _planRunSource.SetupSequence(s => s.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs)
            .ReturnsAsync(PlanRunTraces.Empty);
        return new(_graph.Object, erasers!, _retained, _ambientScope, TimeProvider.System,
            NullLogger<PersonalDataErasureCoordinator>.Instance, _conversations.Object, _planRunSource.Object);
    }

    /// <summary>An eraser that records the subject it was handed, keyed by conversation like every shipped one.</summary>
    private Mock<IPersonalDataEraser> RecordingEraser(string name, ErasureStage stage, List<ErasureSubject> seen)
    {
        var eraser = EraserMock(name, stage);
        eraser.SetupGet(e => e.ErasureReach).Returns(ErasureReach.NamedConversations);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Callback<ErasureSubject, CancellationToken>((s, _) => { _calls.Add(name); seen.Add(s); })
            .ReturnsAsync(1);
        return eraser;
    }

    [Fact]
    public async Task PlannedTaskRuns_AreReadBeforeAnythingIsDeleted_AndEveryStoreIsGivenThem()
    {
        // #906. The plan store leads to the runs' step sessions, spills and budgets; read after a delete, it might lead
        // nowhere.
        var subjects = new List<ErasureSubject>();
        var spills = RecordingEraser("spills", ErasureStage.BeforeConversations, subjects);
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => _planRunSource.Verify(s => s.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()), Times.Once))
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Full));

        await CreateWithPlanRuns(Runs, spills.Object, Eraser("plans", ErasureStage.PlannedTasks))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "plans");
        var subject = subjects.Should().ContainSingle().Subject;
        subject.PlanRuns.Should().BeSameAs(Runs);
        subject.AllConversationIds.Should().Equal("c-1", "c-2", "plan-1:step-a");
        subject.ConversationIds.Should().Equal(["c-1", "c-2"], "the conversation stores must not be handed a step's id");
    }

    [Fact]
    public async Task ThePlans_AreErasedAfterTheConversations_AndBeforeTheStageAfter()
    {
        await CreateWithPlanRuns(Runs,
                Eraser("ledger", ErasureStage.AfterConversations),
                Eraser("plans", ErasureStage.PlannedTasks),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations", "plans", "ledger");
    }

    [Fact]
    public async Task AStoreReachedThroughThePlansFails_ThePlansAndTheStageAfterAreHeldBack()
    {
        // Deleting the plans after a failure would leave the failed store's step records with nothing that leads to them.
        var receipt = await CreateWithPlanRuns(Runs,
                Eraser("observability", ErasureStage.BeforeConversations, failure: new InvalidOperationException("down")),
                Eraser("plans", ErasureStage.PlannedTasks),
                Eraser("budgets", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("observability");
        receipt.Stores.Select(s => (s.Store, s.Status)).Should().Equal(
            ("observability", StoreErasureStatus.Failed),
            ("plans", StoreErasureStatus.Skipped),
            ("budgets", StoreErasureStatus.Skipped));
    }

    [Fact]
    public async Task ThePlansFail_TheStageAfterStillClearsTheErasedConversations_ButNotThePlannedTasks()
    {
        // The conversations are gone; held back, their ledger rows could never be found again. The plans are not, so
        // their steps' ledger rows must stay until a retry erases them.
        var seen = new List<ErasureSubject>();
        var receipt = await CreateWithPlanRuns(Runs,
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("plans", ErasureStage.PlannedTasks, failure: new InvalidOperationException("locked")),
                RecordingEraser("ledger", ErasureStage.AfterConversations, seen).Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

        var after = seen.Should().ContainSingle().Subject;
        after.AllConversationIds.Should().Equal("c-1", "c-2");
        after.PlanRuns.IsEmpty.Should().BeTrue();
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial, "the plans' own line failed");
        var ledger = receipt.Stores.Single(s => s.Store == "ledger");
        ledger.Status.Should().Be(StoreErasureStatus.Skipped, "the ledger still holds the planned tasks' rows");
        ledger.Reason.Should().StartWith("partly kept");
    }

    [Fact]
    public async Task TheConversationsFail_TheStageAfterStillClearsTheErasedPlannedTasks_ButNotTheConversations()
    {
        var seen = new List<ErasureSubject>();
        await CreateWithPlanRuns(Runs,
                Eraser("conversations", ErasureStage.Conversations, failure: new InvalidOperationException("locked")),
                Eraser("plans", ErasureStage.PlannedTasks),
                RecordingEraser("ledger", ErasureStage.AfterConversations, seen).Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

        var after = seen.Should().ContainSingle().Subject;
        after.ConversationIds.Should().BeEmpty();
        after.AllConversationIds.Should().Equal("plan-1:step-a");
        after.PlanRuns.RunBudgetKeys.Should().Equal("planrun:plan-1");
    }

    [Fact]
    public async Task APlannedTaskStillRecordedAfterThePlansStage_IsLeftOutOfTheStageAfter()
    {
        // A run in progress, or made meanwhile: its plan was kept, and its budget and once-only claims are live. Clearing
        // them would reset its token ceiling and let a once-only tool run again inside it.
        var seen = new List<ErasureSubject>();
        var coordinator = CreateWithPlanRuns(
            new PlanRunTraces(["plan-1:step-a", "plan-2:step-b"], [], ["planrun:plan-1", "planrun:plan-2"]),
            Eraser("conversations", ErasureStage.Conversations),
            Eraser("plans", ErasureStage.PlannedTasks),
            RecordingEraser("ledger", ErasureStage.AfterConversations, seen).Object);
        _planRunSource.SetupSequence(s => s.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanRunTraces(["plan-1:step-a", "plan-2:step-b"], [], ["planrun:plan-1", "planrun:plan-2"]))
            .ReturnsAsync(new PlanRunTraces(["plan-2:step-b"], [], ["planrun:plan-2"]));

        var receipt = await coordinator.EraseOwnerAsync(Owner, CancellationToken.None);

        var after = seen.Should().ContainSingle().Subject;
        after.AllConversationIds.Should().Equal("c-1", "c-2", "plan-1:step-a");
        after.PlanRuns.RunBudgetKeys.Should().Equal("planrun:plan-1");
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task ThePlansCannotBeReadAgain_TheirIdsAreLeftOutOfTheStageAfter_AndTheReceiptSaysSo()
    {
        // The deletes are done, so the receipt must still come back; without the second reading nothing shows which runs
        // are still live, so none of the planned tasks' ids is cleared.
        var seen = new List<ErasureSubject>();
        var coordinator = CreateWithPlanRuns(Runs,
            Eraser("conversations", ErasureStage.Conversations),
            Eraser("plans", ErasureStage.PlannedTasks),
            RecordingEraser("ledger", ErasureStage.AfterConversations, seen).Object);
        _planRunSource.SetupSequence(s => s.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Runs)
            .ThrowsAsync(new InvalidOperationException("C:\\secret\\planner.db locked"));

        var receipt = await coordinator.EraseOwnerAsync(Owner, CancellationToken.None);

        seen.Should().ContainSingle().Which.PlanRuns.IsEmpty.Should().BeTrue();
        receipt.Stores.Should().Contain(s => s.Store == "planned tasks run during erasure" && s.Status == StoreErasureStatus.Failed);
        receipt.CompletenessReason.Should().NotContain("secret");
    }

    [Fact]
    public async Task OnlyPartOfTheStageAfterMayRun_AStoreThatErasesTheWholePersonIsHeldBack()
    {
        // It cannot be narrowed to the ids of the lead that is gone, so running it would clear what the surviving lead
        // still leads to.
        var receipt = await CreateWithPlanRuns(Runs,
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("plans", ErasureStage.PlannedTasks, failure: new InvalidOperationException("locked")),
                Eraser("owner-wide", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().NotContain("owner-wide");
        receipt.Stores.Single(s => s.Store == "owner-wide").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task BothLeadsFail_TheStageAfterIsHeldBack()
    {
        var receipt = await CreateWithPlanRuns(Runs,
                Eraser("conversations", ErasureStage.Conversations, failure: new InvalidOperationException("locked")),
                Eraser("plans", ErasureStage.PlannedTasks, failure: new InvalidOperationException("locked")),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task APlannedTaskRunDuringErasure_IsKeptAndTheReceiptSaysToRunAgain()
    {
        // The plan store keeps a run recorded after it was read; the second reading finds it.
        var coordinator = CreateWithPlanRuns(Runs, Eraser("plans", ErasureStage.PlannedTasks));
        _planRunSource.SetupSequence(s => s.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Runs)
            .ReturnsAsync(new PlanRunTraces([], ["job-new"], ["planrun:plan-1"]));

        var receipt = await coordinator.EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().Contain(s => s.Store == "planned tasks run during erasure" && s.Status == StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task NoPlannedTaskRunDuringErasure_TheReceiptIsFull()
    {
        var receipt = await CreateWithPlanRuns(Runs, Eraser("plans", ErasureStage.PlannedTasks))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task StagesRunInOrder_RegardlessOfRegistrationOrder()
    {
        await Create(
                Eraser("ledger", ErasureStage.AfterConversations),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations", "ledger");
    }

    [Fact]
    public async Task DeletedBytesCannotBeCleared_ItIsItsOwnFailedLine_AndNoStageIsHeldBack()
    {
        // The delete is already committed when its bytes are cleared. Failing the conversations' own line would hold the
        // ledger back, and a retry would find no conversations to lead it to the ledger rows.
        var receipt = await Create(
                ScrubbingEraser("conversations", ErasureStage.Conversations, removed: 2,
                    scrubFailure: new InvalidOperationException("C:\\secret\\conversations.db busy")),
                Eraser("ledger", ErasureStage.AfterConversations, removed: 4))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("conversations", "ledger", "scrub conversations");
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Select(s => (s.Store, s.Status)).Should().Equal(
            ("conversations", StoreErasureStatus.Erased),
            ("ledger", StoreErasureStatus.Erased),
            ("conversations (deleted bytes)", StoreErasureStatus.Failed));
        receipt.CompletenessReason.Should().NotContain("secret", "receipts are returned to the caller; exception text is not");
    }

    [Fact]
    public async Task DeletedBytesAreClearedOnEveryRequest_EvenOneThatDeletesNothing()
    {
        // A retry after a failed clear finds nothing left to delete; it must still clear what the first request left.
        var receipt = await Create(
                ScrubbingEraser("spills", ErasureStage.BeforeConversations, removed: 0),
                Eraser("conversations", ErasureStage.Conversations, removed: 0))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations", "scrub spills");
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
    }

    [Fact]
    public async Task DeletedBytesAreCleared_EvenWhenAnEarlierStoreFailed()
    {
        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, failure: new IOException()),
                ScrubbingEraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "scrub conversations");
        receipt.Stores.Select(s => s.Store).Should().NotContain("conversations (deleted bytes)");
    }

    [Fact]
    public async Task ConversationIds_AreReadBeforeAnythingIsErased_AndHandedToEveryStore()
    {
        ErasureSubject? seen = null;
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns("spills");
        eraser.SetupGet(e => e.ErasureStage).Returns(ErasureStage.BeforeConversations);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .Callback<ErasureSubject, CancellationToken>((s, _) => seen = s)
            .ReturnsAsync(0);

        await Create(eraser.Object).EraseOwnerAsync(Owner, CancellationToken.None);

        seen!.ConversationIds.Should().Equal("c-1", "c-2");
        seen.OwnerId.Should().Be(Owner);
        seen.CanonicalOwnerId.Should().Be("owner-1");
    }

    [Fact]
    public async Task AStoreReachedThroughConversationsFails_ConversationsAndLedgerAreHeldBack()
    {
        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations, failure: new IOException("C:\\secret\\path locked")),
                Eraser("plans", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "plans");
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Select(s => (s.Store, s.Status)).Should().Equal(
            ("spills", StoreErasureStatus.Failed),
            ("plans", StoreErasureStatus.Erased),
            ("conversations", StoreErasureStatus.Skipped),
            ("ledger", StoreErasureStatus.Skipped));
        receipt.CompletenessReason.Should().NotContain("secret", "receipts are returned to the caller; exception text is not");
    }

    [Fact]
    public async Task TheConversationStoreFails_TheLedgerIsHeldBack()
    {
        var receipt = await Create(
                Eraser("conversations", ErasureStage.Conversations, failure: new InvalidOperationException()),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("conversations");
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task NoConversationEraserButConversationsExist_TheLedgerIsHeldBack()
    {
        // A host whose conversation store cannot erase leaves the conversations standing; removing their ledger rows
        // would let a once-only tool run again in them.
        var receipt = await Create(Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().BeEmpty();
        receipt.Stores.Single().Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task NoConversationsAtAll_TheLedgerStillRuns()
    {
        _conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Create(Eraser("ledger", ErasureStage.AfterConversations)).EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("ledger");
    }

    [Fact]
    public async Task TheGraphThrows_OtherStoresStillRun_AndTheReceiptSaysPartial()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("neo4j://user:pass@host"));

        var receipt = await Create(
                Eraser("spills", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().Equal("spills", "conversations");
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores[0].Should().Be(new StoreErasureOutcome(
            "knowledge graph", StoreErasureStatus.Failed, 0, "the store could not be erased; the failure has been logged"));
        receipt.CompletenessReason.Should().NotContain("pass");
    }

    [Fact]
    public async Task TheGraphIsPartial_TheCombinedReceiptIsPartialAndKeepsItsReason()
    {
        _graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GraphReceipt(ErasureCompleteness.Partial, "remote memory could not be purged"));

        var receipt = await Create(Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.CompletenessReason.Should().Contain("remote memory could not be purged");
    }

    [Fact]
    public async Task ListingConversationsFails_NothingIsErased()
    {
        _conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException());
        var coordinator = Create(Eraser("spills", ErasureStage.BeforeConversations));

        var act = () => coordinator.EraseOwnerAsync(Owner, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _calls.Should().BeEmpty();
        _graph.Verify(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankOwner_IsRefused_NeverAWildcard(string owner)
    {
        var act = () => Create(Eraser("spills", ErasureStage.BeforeConversations)).EraseOwnerAsync(owner, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AConfiguredStoreThatCannotErase_IsOnTheReceipt_AndDoesNotHoldTheConversationsBack()
    {
        var receipt = await Create(
                new NonErasableStore("CustomVectorStore", ErasureStage.BeforeConversations),
                Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().Contain(new StoreErasureOutcome(
            "CustomVectorStore", StoreErasureStatus.NotSupported, 0, NonErasableStore.Reason));
        _calls.Should().ContainSingle().Which.Should().Be(
            "conversations", "a retry would not change it, so holding the conversations back gains nothing");
    }

    [Fact]
    public async Task AConversationStoreThatCannotErase_HoldsBackTheStageAfterIt()
    {
        var receipt = await Create(
                new NonErasableStore("CustomConversationStore", ErasureStage.Conversations),
                Eraser("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        _calls.Should().BeEmpty();
        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Single(s => s.Store == "ledger").Status.Should().Be(StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task AConversationStartedDuringErasure_IsReportedNotClaimedErased()
    {
        _conversations.SetupSequence(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation("c-1")])
            .ReturnsAsync([Conversation("c-new")]);

        var receipt = await Create(Eraser("conversations", ErasureStage.Conversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Completeness.Should().Be(ErasureCompleteness.Partial);
        receipt.Stores.Should().Contain(s => s.Store == "conversations started during erasure"
            && s.Status == StoreErasureStatus.Skipped);
    }

    [Fact]
    public async Task TheCallerGoingAway_DoesNotInterruptTheConversationsOrTheStageAfterThem()
    {
        using var caller = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        IPersonalDataEraser Capturing(string name, ErasureStage stage, Action? onRun = null)
        {
            var eraser = new Mock<IPersonalDataEraser>();
            eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
            eraser.SetupGet(e => e.ErasureStage).Returns(stage);
            eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
                .Callback<ErasureSubject, CancellationToken>((_, token) => { tokens.Add(token); onRun?.Invoke(); })
                .ReturnsAsync(0);
            return eraser.Object;
        }

        await Create(
                Capturing("spills", ErasureStage.BeforeConversations, onRun: caller.Cancel),
                Capturing("conversations", ErasureStage.Conversations),
                Capturing("ledger", ErasureStage.AfterConversations))
            .EraseOwnerAsync(Owner, caller.Token);

        tokens.Should().HaveCount(3);
        tokens.Skip(1).Should().OnlyContain(t => !t.CanBeCanceled,
            "once conversations start being deleted, stopping halfway would orphan what depends on them");
    }

    [Fact]
    public async Task AForwardedRegistrationThatCannotErase_IsSkipped()
    {
        // AddPersonalDataEraser yields null when the host's store does not implement the interface.
        var receipt = await Create(null, Eraser("spills", ErasureStage.BeforeConversations))
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle().Which.Store.Should().Be("spills");
    }
}
