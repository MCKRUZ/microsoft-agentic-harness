using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// The export's manifest and failure rules (#902): every store that returned data has a section, every store erasure
/// reaches or lists that did not is named with why, and the copy never reads as complete while one of them left the
/// person's data out.
/// </summary>
public sealed class PersonalDataExportCoordinatorTests
{
    private const string Owner = "Owner-1";
    private readonly AmbientRequestScope _ambientScope = new();

    private PersonalDataExportCoordinator Create(
        IEnumerable<IPersonalDataExporter>? exporters = null,
        IEnumerable<IPersonalDataEraser>? erasers = null,
        IEnumerable<RetainedPersonalData>? retained = null,
        IConversationStore? conversations = null,
        IErasureJournal? journal = null,
        ICrossSessionMemoryStore? memory = null,
        IPlanRunTraceSource? planRuns = null) =>
        new(exporters ?? [], erasers ?? [], retained ?? [], _ambientScope, TimeProvider.System,
            NullLogger<PersonalDataExportCoordinator>.Instance, conversations, journal, memory, planRuns);

    private static Mock<IPersonalDataExporter> RecordingExporter(Action<ExportSubject> seen)
    {
        var exporter = Exporter("spills");
        exporter.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .Callback<ExportSubject, CancellationToken>((s, _) => seen(s))
            .ReturnsAsync([]);
        return exporter;
    }

    private static Mock<IPersonalDataExporter> Exporter(string name, params string[] records)
    {
        var exporter = new Mock<IPersonalDataExporter>();
        exporter.SetupGet(e => e.ExportStoreName).Returns(name);
        exporter.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. records.Select(r => JsonSerializer.SerializeToElement(new { value = r }))]);
        return exporter;
    }

    private static IPersonalDataEraser Eraser(string name)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        return eraser.Object;
    }

    [Fact]
    public async Task EachExporter_GivesASectionAndAnIncludedLineWithItsCount()
    {
        var copy = await Create([Exporter("conversations", "a", "b").Object, Exporter("schedules").Object])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Sections.Select(s => (s.Store, s.Records.Count)).Should().Equal(("conversations", 2), ("schedules", 0));
        copy.Manifest.Should().Equal(
            new ExportManifestEntry("conversations", ExportStatus.Included, 2),
            new ExportManifestEntry("schedules", ExportStatus.Included, 0));
        copy.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task OneExporterFails_ItsLineSaysFailedWithoutDetail_TheOthersStillAnswer_AndTheCopyIsIncomplete()
    {
        var broken = Exporter("broken");
        broken.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Server=db;Password=secret"));

        var copy = await Create([broken.Object, Exporter("schedules", "s").Object])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Sections.Select(s => s.Store).Should().Equal("schedules");
        var line = copy.Manifest.Single(m => m.Store == "broken");
        line.Status.Should().Be(ExportStatus.Failed);
        line.Reason.Should().NotContain("secret");
        copy.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task TheCallerCancels_TheExportStopsRatherThanReportingAFailedStore()
    {
        var cancelled = Exporter("conversations");
        cancelled.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Create([cancelled.Object]).ExportOwnerAsync(Owner, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AStoreErasureReachesThatDoesNotExport_IsNamed_AndTheCopyIsIncomplete()
    {
        var copy = await Create([Exporter("schedules").Object], [Eraser("plans")])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle(m => m.Store == "plans")
            .Which.Status.Should().Be(ExportStatus.NotExportable);
        copy.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task AStoreThatBothErasesAndExports_HasOneLine()
    {
        var store = Exporter("conversations", "c");
        store.As<IPersonalDataEraser>().SetupGet(e => e.ErasureStoreName).Returns("conversations");

        var copy = await Create([store.Object], [(IPersonalDataEraser)store.Object])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle(m => m.Store == "conversations")
            .Which.Status.Should().Be(ExportStatus.Included);
    }

    [Fact]
    public async Task AStandInForAStoreThatCannotErase_IsNotListedWhenThatStoreExports()
    {
        var exporter = new ReplacementStore();

        var copy = await Create([exporter], [new NonErasableStore(nameof(ReplacementStore), ErasureStage.Conversations)])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle().Which.Status.Should().Be(ExportStatus.Included);
    }

    [Fact]
    public async Task AStoreThatHoldsNothingAPersonWouldReceive_IsListedAsNotApplicable_AndLeavesTheCopyComplete()
    {
        var copy = await Create(erasers: [new CounterStore()]).ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle().Which.Status.Should().Be(ExportStatus.NotApplicable);
        copy.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task StoresThatCannotBeSearched_AreListedWithHowLongTheyKeepData()
    {
        var copy = await Create(retained:
            [
                new RetainedPersonalData("application logs", new UnboundedLog()),
                new RetainedPersonalData("response cache", new VolatileCache()),
            ])
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Select(m => (m.Store, m.Status)).Should().Equal(
            ("application logs", ExportStatus.RetainedUnsearchable),
            ("response cache", ExportStatus.RetainedUntilExpiry));
        copy.Manifest.Should().OnlyContain(m => m.Reason != null);
        copy.IsComplete.Should().BeFalse("nothing ages the logs out, so the person's data in them is left out for good");
    }

    [Fact]
    public async Task CrossSessionMemoryThatCannotExport_IsNamed()
    {
        var copy = await Create(memory: Mock.Of<ICrossSessionMemoryStore>()).ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle(m => m.Store == "cross-session memories")
            .Which.Status.Should().Be(ExportStatus.NotExportable);
    }

    [Fact]
    public async Task ConversationsThatCouldNotBeRead_AreAFailedLine()
    {
        var conversations = new Mock<IConversationStore>();
        conversations.As<IConversationIdSource>()
            .Setup(s => s.ListConversationIdsAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationIdListing(["c-1"], 2));

        var copy = await Create(conversations: conversations.Object).ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle().Which.Status.Should().Be(ExportStatus.Failed);
        copy.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ListingTheConversationsFails_ItIsAFailedLine_AndTheStoresStillAnswer()
    {
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        var copy = await Create([Exporter("schedules", "s").Object], conversations: conversations.Object)
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().Contain(m => m.Store == "conversation list" && m.Status == ExportStatus.Failed);
        copy.Sections.Should().ContainSingle(s => s.Store == "schedules");
    }

    [Fact]
    public async Task EachExporter_IsGivenTheSubjectsOwnerFormsAndConversations_WithoutOnesShapedLikeAPlannedTask()
    {
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation("c-1"), Conversation("plan:step")]);
        ExportSubject? seen = null;
        var exporter = Exporter("spills");
        exporter.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .Callback<ExportSubject, CancellationToken>((s, _) => seen = s)
            .ReturnsAsync([]);

        await Create([exporter.Object], conversations: conversations.Object).ExportOwnerAsync(Owner, CancellationToken.None);

        seen!.OwnerId.Should().Be(Owner);
        seen.CanonicalOwnerId.Should().Be("owner-1");
        seen.ConversationIds.Should().Equal("c-1");
    }

    [Fact]
    public async Task TwoRegistrationsOfOneStore_AreReadOnce_AndNeitherIsNamedAsLeftOut()
    {
        // Azure AI Search backs the vector and BM25 stores with one index: both erase and export, under one export name.
        var vector = new SharedIndexStore("document index (Azure AI Search vectors)");
        var keyword = new SharedIndexStore("document index (Azure AI Search full text)");

        var copy = await Create([vector, keyword], erasers: [vector, keyword]).ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Sections.Should().ContainSingle(s => s.Store == SharedIndexStore.Name).Which.Records.Should().HaveCount(1,
            "the person's records must not appear twice");
        (vector.Reads + keyword.Reads).Should().Be(1);
        copy.Manifest.Should().ContainSingle(m => m.Store == SharedIndexStore.Name && m.Status == ExportStatus.Included);
        copy.Manifest.Should().NotContain(m => m.Status == ExportStatus.NotExportable);
        copy.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task PlannedTaskRuns_AndWhatAnUnfinishedErasureRecorded_ReachEveryExporter()
    {
        // #906/#907: a planned task's model steps ran as conversations of their own and its tool steps under the run's id,
        // and an erasure that failed part-way may already have deleted the conversation or plan that led to what it left.
        // Erasure reaches all of it, so the copy must too.
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(c => c.ListAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync([Conversation("c-1")]);
        var planRuns = new Mock<IPlanRunTraceSource>();
        planRuns.Setup(p => p.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanRunTraces(["run-1:step-1"], ["job-1"], ["planrun:run-1"]));
        var journal = new Mock<IErasureJournal>();
        journal.Setup(j => j.BeginRequestAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());
        journal.Setup(j => j.ReadAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(new ErasureJournalEntries(
            ["c-gone"], [], new PlanRunTraces(["run-gone:step-1"], ["job-gone"], ["planrun:run-gone"])));
        ExportSubject? seen = null;

        await Create([RecordingExporter(s => seen = s).Object], conversations: conversations.Object, journal: journal.Object,
            planRuns: planRuns.Object).ExportOwnerAsync(Owner, CancellationToken.None);

        seen!.ConversationIds.Should().BeEquivalentTo(["c-1", "c-gone"]);
        seen.AllConversationIds.Should().BeEquivalentTo(["c-1", "c-gone", "run-1:step-1", "run-gone:step-1"],
            "a store keyed by conversation id reads the planned tasks' model steps too");
        seen.PlanRuns.ToolResultScopeIds.Should().BeEquivalentTo(["job-1", "job-gone"]);
    }

    [Fact]
    public async Task ReadingThePlannedTaskRunsFails_ItIsAFailedLine_AndTheStoresStillAnswer()
    {
        var planRuns = new Mock<IPlanRunTraceSource>();
        planRuns.Setup(p => p.ListRunTracesAsync(Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Data Source=/srv/planner.db"));

        var copy = await Create([Exporter("schedules", "s").Object], planRuns: planRuns.Object)
            .ExportOwnerAsync(Owner, CancellationToken.None);

        var line = copy.Manifest.Single(m => m.Store == "planned-task run list");
        line.Status.Should().Be(ExportStatus.Failed, "what only the runs lead to is missing from the copy");
        line.Reason.Should().NotContain("planner.db");
        copy.Sections.Should().ContainSingle(s => s.Store == "schedules");
        copy.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task ReadingTheErasureJournalFails_ItIsAFailedLine_AndTheStoresStillAnswer()
    {
        var journal = new Mock<IErasureJournal>();
        journal.Setup(j => j.BeginRequestAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());
        journal.Setup(j => j.ReadAsync(Owner, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));

        var copy = await Create([Exporter("schedules", "s").Object], journal: journal.Object)
            .ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().Contain(m => m.Store == "unfinished erasure requests" && m.Status == ExportStatus.Failed);
        copy.Sections.Should().ContainSingle(s => s.Store == "schedules");
        copy.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task CallerHasARequestScope_EveryStoreIsReadSystemScoped_UnderThePersonsErasureTurn()
    {
        var callerScope = new ServiceCollection().BuildServiceProvider();
        IServiceProvider? seenByStore = callerScope;
        var turnHeld = false;
        var turn = new Mock<IDisposable>();
        turn.Setup(t => t.Dispose()).Callback(() => turnHeld = false);
        var journal = new Mock<IErasureJournal>();
        journal.Setup(j => j.BeginRequestAsync(Owner, It.IsAny<CancellationToken>()))
            .Callback(() => turnHeld = true)
            .ReturnsAsync(turn.Object);
        journal.Setup(j => j.ReadAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(ErasureJournalEntries.Empty);
        var heldDuringRead = false;
        var exporter = Exporter("schedules");
        exporter.Setup(e => e.ExportSubjectAsync(It.IsAny<ExportSubject>(), It.IsAny<CancellationToken>()))
            .Callback(() => (seenByStore, heldDuringRead) = (_ambientScope.Current, turnHeld))
            .ReturnsAsync([]);

        using (_ambientScope.BeginScope(callerScope))
        {
            await Create([exporter.Object], journal: journal.Object).ExportOwnerAsync(Owner, CancellationToken.None);
            _ambientScope.Current.Should().BeSameAs(callerScope, "the caller's scope is restored afterwards");
        }

        seenByStore.Should().BeNull("a scope-filtering store would otherwise answer for the caller, not the subject");
        heldDuringRead.Should().BeTrue("a copy must not be made while an erasure for the same person is deleting");
        turnHeld.Should().BeFalse("the turn is given back when the copy is made");
    }

    private static ConversationRecord Conversation(string id) =>
        new(id, "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);

    /// <summary>One of two registrations that front the same physical store, erasing under its own name.</summary>
    private sealed class SharedIndexStore(string erasureName) : IPersonalDataEraser, IPersonalDataExporter
    {
        public const string Name = "document index (Azure AI Search)";

        public int Reads { get; private set; }

        public string ErasureStoreName => erasureName;

        public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

        public ErasureReach ErasureReach => ErasureReach.Owner;

        public string ExportStoreName => Name;

        public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<JsonElement>>([JsonSerializer.SerializeToElement(new { id = "chunk-1" })]);
        }
    }

    /// <summary>A deployer's replacement store that can export, shadowed on the erasure side by a stand-in.</summary>
    private sealed class ReplacementStore : IPersonalDataExporter
    {
        public string ExportStoreName => "conversations";

        public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonElement>>([]);
    }

    [PersonalDataStore("operational counters", PersonalDataCategories.Identifiers, PersonalDataKey.Owner,
        RetentionBasis.Configured, ErasureSupport.PurgeByOwner, ExportSupport.NotApplicable)]
    private sealed class CounterStore : IPersonalDataEraser
    {
        public string ErasureStoreName => "conversation token budgets";

        public ErasureStage ErasureStage => ErasureStage.AfterConversations;

        public ErasureReach ErasureReach => ErasureReach.NamedConversations;

        public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    [PersonalDataStore("logs", PersonalDataCategories.Identifiers, PersonalDataKey.Unkeyed,
        RetentionBasis.Unbounded, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class UnboundedLog;

    [PersonalDataStore("cache", PersonalDataCategories.Identifiers, PersonalDataKey.Unkeyed,
        RetentionBasis.Volatile, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class VolatileCache;
}
