using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
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

    private static Mock<IPersonalDataEraser> UnconfiguredEraser()
    {
        var eraser = new Mock<IPersonalDataEraser>(MockBehavior.Strict);
        eraser.SetupGet(e => e.ErasureStoreName).Returns(IndexName);
        eraser.SetupGet(e => e.ErasureStage).Returns(ErasureStage.BeforeConversations);
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

    private static PersonalDataErasureCoordinator Coordinator(params IPersonalDataEraser[] erasers)
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
            NullLogger<PersonalDataErasureCoordinator>.Instance);
    }

    [Fact]
    public async Task Erasure_AnUnconfiguredStore_IsListedAsNotConfigured_WithoutBeingAsked_AndTheReceiptStaysFull()
    {
        var index = UnconfiguredEraser();
        var after = Eraser("conversation-reached store", ErasureStage.AfterConversations);

        var receipt = await Coordinator(index.Object, after.Object).EraseOwnerAsync(Owner, CancellationToken.None);

        receipt.Stores.Should().ContainSingle(s => s.Store == IndexName)
            .Which.Should().Match<StoreErasureOutcome>(s => s.Status == StoreErasureStatus.NotConfigured
                && s.ItemsRemoved == 0 && s.Reason!.Contains("not configured"));
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
                && m.Reason!.Contains("not configured"));
        copy.Sections.Should().BeEmpty();
        copy.IsComplete.Should().BeTrue();
    }
}
