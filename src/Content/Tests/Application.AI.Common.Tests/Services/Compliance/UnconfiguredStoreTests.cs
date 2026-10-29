using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// A store a host registers but does not configure, such as an Azure AI Search index with no endpoint, has never held
/// anything (#1003). The receipt and the copy say it was not configured rather than reading as a search that found
/// nothing, and neither asks it, holds anything back, or reads as incomplete because of it.
/// </summary>
public sealed class UnconfiguredStoreTests
{
    private const string Owner = "owner-1";
    private const string IndexName = "document index (Azure AI Search vectors)";

    private static Mock<IPersonalDataEraser> UnconfiguredEraser(
        ErasureStage stage = ErasureStage.BeforeConversations, string name = IndexName)
    {
        var eraser = new Mock<IPersonalDataEraser>(MockBehavior.Strict);
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.SetupGet(e => e.ErasureReach).Returns(ErasureReach.Owner);
        eraser.As<IConfigurablePersonalDataStore>().SetupGet(s => s.IsConfigured).Returns(false);
        return eraser;
    }

    private static Mock<IPersonalDataEraser> Eraser(string name, ErasureStage stage)
    {
        var eraser = new Mock<IPersonalDataEraser>();
        eraser.SetupGet(e => e.ErasureStoreName).Returns(name);
        eraser.SetupGet(e => e.ErasureStage).Returns(stage);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return eraser;
    }

    private static PersonalDataErasureCoordinator Coordinator(params IPersonalDataEraser[] erasers) =>
        Coordinator(null, null, erasers);

    private static PersonalDataErasureCoordinator Coordinator(
        IConversationStore? conversations, IErasureJournal? journal, params IPersonalDataEraser[] erasers)
    {
        var graph = new Mock<IErasureOrchestrator>();
        graph.Setup(g => g.EraseByOwnerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ErasureReceipt
            {
                RequestId = "r",
                ScopeId = Owner,
                RequestedAt = DateTimeOffset.UnixEpoch,
                CompletedAt = DateTimeOffset.UnixEpoch,
                NodesDeleted = 0,
                EdgesDeleted = 0,
                FeedbackWeightsDeleted = 0,
                VectorEmbeddingsDeleted = 0,
            });
        return new PersonalDataErasureCoordinator(graph.Object, erasers, [], new AmbientRequestScope(), TimeProvider.System,
            NullLogger<PersonalDataErasureCoordinator>.Instance, conversations, journal: journal);
    }

    private static Mock<IPersonalDataEraser> FailingEraser()
    {
        var eraser = Eraser("failing store", ErasureStage.BeforeConversations);
        eraser.Setup(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("down"));
        return eraser;
    }

    public static TheoryData<ErasureStage> LaterStages => new()
    {
        ErasureStage.Conversations, ErasureStage.AfterConversations,
    };

    [Theory]
    [MemberData(nameof(LaterStages))]
    public async Task Erasure_AnUnconfiguredStoreInAHeldBackStage_IsNotConfigured_NotHeldBack(ErasureStage stage)
    {
        // An earlier store failed, so the later stages are held back for a retry. A store with no backend holds nothing a
        // retry could find, so its line must not say it was kept.
        var unconfigured = UnconfiguredEraser(stage, "unconfigured later store");

        var receipt = await Coordinator(FailingEraser().Object, unconfigured.Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle(s => s.Store == "unconfigured later store")
            .Which.Status.Should().Be(StoreErasureStatus.NotConfigured);
    }

    [Fact]
    public async Task Erasure_AnUnconfiguredStore_LetsTheRequestClearItsJournal()
    {
        // The journal keeps the ids a request read until every store has erased them; an unconfigured store counts as done,
        // or every request on a host with one would leave the person's ids behind.
        var conversations = new Mock<IConversationStore>();
        conversations.SetupSequence(c => c.ListAsync(Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ConversationRecord("c-1", "agent", Owner, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [])])
            .ReturnsAsync([]);
        var journal = new Mock<IErasureJournal>();
        journal.Setup(j => j.BeginRequestAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());
        journal.Setup(j => j.ReadAsync(Owner, It.IsAny<CancellationToken>())).ReturnsAsync(ErasureJournalEntries.Empty);

        await Coordinator(conversations.Object, journal.Object, UnconfiguredEraser().Object)
            .EraseOwnerAsync(Owner, CancellationToken.None);

        journal.Verify(j => j.RecordAsync(Owner, It.IsAny<string>(), It.IsAny<ErasureJournalEntries>(), It.IsAny<CancellationToken>()),
            Times.Once, "the request must have recorded something to clear");
        journal.Verify(j => j.ClearAsync(Owner, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Erasure_AnUnconfiguredStore_IsListedAsNotConfigured_WithoutBeingAsked_AndTheReceiptStaysFull()
    {
        var index = UnconfiguredEraser();
        var after = Eraser("conversation-reached store", ErasureStage.AfterConversations);

        var receipt = await Coordinator(index.Object, after.Object).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle(s => s.Store == IndexName)
            .Which.Should().Match<StoreErasureOutcome>(s => s.Status == StoreErasureStatus.NotConfigured
                && s.ItemsRemoved == 0 && s.Reason == IConfigurablePersonalDataStore.NotConfiguredReason);
        receipt.Completeness.Should().Be(ErasureCompleteness.Full);
        after.Verify(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()), Times.Once,
            "an unconfigured store must not hold the later stages back");
        index.Verify(e => e.EraseSubjectAsync(It.IsAny<ErasureSubject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Erasure_AConfiguredStore_IsAskedAsUsual()
    {
        var index = Eraser(IndexName, ErasureStage.BeforeConversations);
        index.As<IConfigurablePersonalDataStore>().SetupGet(s => s.IsConfigured).Returns(true);

        var receipt = await Coordinator(index.Object).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle(s => s.Store == IndexName).Which.Status.Should().Be(StoreErasureStatus.Erased);
    }

    [Fact]
    public async Task Export_AnUnconfiguredStore_IsNotApplicable_WithoutBeingRead_AndTheCopyStaysComplete()
    {
        var exporter = new Mock<IPersonalDataExporter>(MockBehavior.Strict);
        exporter.SetupGet(e => e.ExportStoreName).Returns(IndexName);
        exporter.SetupGet(e => e.SharedStoreKey).Returns((string?)null);
        exporter.As<IConfigurablePersonalDataStore>().SetupGet(s => s.IsConfigured).Returns(false);
        var coordinator = new PersonalDataExportCoordinator([exporter.Object], [], [], new AmbientRequestScope(),
            TimeProvider.System, NullLogger<PersonalDataExportCoordinator>.Instance);

        var copy = await coordinator.ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Manifest.Should().ContainSingle(m => m.Store == IndexName)
            .Which.Should().Match<ExportManifestEntry>(m => m.Status == ExportStatus.NotApplicable
                && m.Reason == IConfigurablePersonalDataStore.NotConfiguredReason);
        copy.Sections.Should().BeEmpty();
        copy.IsComplete.Should().BeTrue();
    }
}
