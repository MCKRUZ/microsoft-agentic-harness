using System.Text.Json;
using Application.AI.Common.Extensions;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Services.Compliance;

/// <summary>
/// A substrate composed twice registers every personal-data registration twice (#1004). One store is still one section of
/// the person's copy, one manifest line and one receipt line, and is erased once.
/// </summary>
/// <remarks>
/// Built with the real registration helpers, because each doubles differently: <c>AddPersonalDataExporter</c> and
/// <c>AddPersonalDataEraser</c> forward twice to the same instance, <c>AddPersonalDataEraser</c> builds a new stand-in for a
/// store that cannot erase on every resolution, and <c>AddRetainedPersonalData</c> builds a new line object every time.
/// </remarks>
public sealed class PersonalDataSubstrateComposedTwiceTests
{
    private const string Owner = "owner-1";
    private const string RetainedLine = "test log";

    private interface IOwnedStore;

    private interface INonErasableStore;

    private interface IRetainedStore;

    [PersonalDataStore("Test store: keyed by owner, erased and exported.", PersonalDataCategories.ConversationContent,
        PersonalDataKey.Owner, RetentionBasis.Configured, ErasureSupport.PurgeByOwner, ExportSupport.ByOwner)]
    private sealed class OwnedStore : IOwnedStore, IPersonalDataEraser, IPersonalDataExporter
    {
        public int Erasures { get; private set; }

        public string ErasureStoreName => "owned store";

        public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

        public ErasureReach ErasureReach => ErasureReach.Owner;

        public string ExportStoreName => "owned store";

        public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
        {
            Erasures++;
            return Task.FromResult(Erasures == 1 ? 1 : 0);
        }

        public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonElement>>([JsonSerializer.SerializeToElement(new { value = "record" })]);
    }

    [PersonalDataStore("Test store: keyed by owner, but this replacement can neither erase nor export.",
        PersonalDataCategories.ConversationContent, PersonalDataKey.Owner, RetentionBasis.Configured,
        ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class ReplacementStore : INonErasableStore;

    [PersonalDataStore("Test log: unkeyed, kept forever.", PersonalDataCategories.ConversationContent,
        PersonalDataKey.Unkeyed, RetentionBasis.Unbounded, ErasureSupport.NotSupported, ExportSupport.NotSupported)]
    private sealed class RetainedStore : IRetainedStore;

    private readonly OwnedStore _owned = new();

    private ServiceProvider ComposeTwice()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOwnedStore>(_owned);
        services.AddSingleton<INonErasableStore, ReplacementStore>();
        services.AddSingleton<IRetainedStore, RetainedStore>();
        for (var i = 0; i < 2; i++)
        {
            services.AddPersonalDataEraser<IOwnedStore>();
            services.AddPersonalDataExporter<IOwnedStore>();
            services.AddPersonalDataEraser<INonErasableStore>();
            services.AddPersonalDataExporter<INonErasableStore>();
            services.AddRetainedPersonalData<IRetainedStore>(RetainedLine);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ExportOwnerAsync_SubstrateComposedTwice_ReadsEachStoreOnceAndNamesItOnce()
    {
        using var provider = ComposeTwice();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var export = new PersonalDataExportCoordinator(
            sp.GetServices<IPersonalDataExporter>(), sp.GetServices<IPersonalDataEraser>(),
            sp.GetServices<RetainedPersonalData>(), new AmbientRequestScope(), TimeProvider.System,
            NullLogger<PersonalDataExportCoordinator>.Instance);

        var copy = await export.ExportOwnerAsync(Owner, CancellationToken.None);

        copy.Sections.Should().ContainSingle(s => s.Store == "owned store").Which.Records.Should().ContainSingle();
        copy.Manifest.GroupBy(m => m.Store).Should().OnlyContain(g => g.Count() == 1,
            "one store registered twice is still one line of the manifest");
        copy.Manifest.Select(m => m.Store).Should().Contain([nameof(ReplacementStore), RetainedLine]);
    }

    [Fact]
    public async Task EraseOwnerAsync_SubstrateComposedTwice_ErasesEachStoreOnceAndListsItOnce()
    {
        using var provider = ComposeTwice();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var erasure = new PersonalDataErasureCoordinator(
            Graph(), sp.GetServices<IPersonalDataEraser>(), sp.GetServices<RetainedPersonalData>(),
            new AmbientRequestScope(), TimeProvider.System, NullLogger<PersonalDataErasureCoordinator>.Instance);

        var receipt = await erasure.EraseOwnerAsync(Owner, CancellationToken.None);

        _owned.Erasures.Should().Be(1);
        receipt.Stores.GroupBy(s => s.Store).Should().OnlyContain(g => g.Count() == 1,
            "one store registered twice is still one line of the receipt");
        receipt.Stores.Select(s => s.Store).Should().Contain(["owned store", nameof(ReplacementStore), RetainedLine]);
    }

    [Fact]
    public void DistinctErasers_OneReplacementTypeAtTwoStages_KeepsBothStandIns()
    {
        // Behind a run store and a plan store, one replacement type is two stand-ins with one name. Collapsing them would
        // drop the PlannedTasks one, and with it the hold-back of what the plan store leads to.
        var erasers = PersonalDataRegistrations.DistinctErasers(
        [
            new NonErasableStore("Replacement", ErasureStage.BeforeConversations),
            new NonErasableStore("Replacement", ErasureStage.PlannedTasks),
            new NonErasableStore("Replacement", ErasureStage.BeforeConversations),
        ]);

        erasers.Select(e => e.ErasureStage).Should().Equal(ErasureStage.BeforeConversations, ErasureStage.PlannedTasks);
    }

    private static IErasureOrchestrator Graph()
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
        return graph.Object;
    }
}
