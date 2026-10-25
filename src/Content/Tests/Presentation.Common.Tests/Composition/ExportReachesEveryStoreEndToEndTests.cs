using System.Reflection;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.Compliance;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Runs;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// #902's done-when, through the real composition: seed every store the export reads for two people, export one, and the
/// copy holds every record of theirs and nothing of the other person's or of anything shared.
/// </summary>
/// <remarks>
/// <para>
/// Exhaustive by construction. <see cref="Seeds"/> holds a seed for each exporter type, and the test first requires every
/// exporter the coordinator actually receives in this composition to have one. A store that learns to export later without
/// an entry fails here, rather than being silently left out of the proof.
/// </para>
/// <para>
/// Each person's records carry a label unique to them. A section passes when it contains the subject's label and neither
/// the other person's nor the shared record's, which is the property that matters: a copy that leaks one other person's
/// record is a disclosure, however complete it is otherwise.
/// </para>
/// </remarks>
public sealed class ExportReachesEveryStoreEndToEndTests : IDisposable
{
    private const string Subject = "Subject-Oid-7F3A";
    private const string Bystander = "bystander-oid-1";

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "export-every-store-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    /// <summary>A person's seeded records, labelled so a copy can be searched for them.</summary>
    private sealed record Seeded(string OwnerId, string Label);

    private static Dictionary<string, Func<IServiceProvider, Seeded, Task>> Seeds => new(StringComparer.Ordinal)
    {
        ["EfCoreConversationStore"] = SeedConversationAsync,
        ["FileSystemConversationStore"] = SeedConversationAsync,
        ["EfScheduleStore"] = (sp, p) => sp.GetRequiredService<IScheduleStore>().CreateAsync(Schedule(p), CancellationToken.None),
        ["KnowledgeGraphExporter"] = SeedGraphAsync,
    };

    [Fact]
    public async Task TheCopy_HoldsEveryRecordOfTheSubjectAndNothingOfAnyoneElse()
    {
        await using var provider = BuildProvider();
        var exporters = await ExportersTheCoordinatorReceivesAsync(provider);
        exporters.Where(name => !Seeds.ContainsKey(name)).Should().BeEmpty(
            "every store the export reads needs a seed here; add an entry for the new store");
        exporters.Should().Contain(["EfCoreConversationStore", "EfScheduleStore", "KnowledgeGraphExporter"],
            "a floor, so a composition that reads fewer stores than this one configures cannot pass");

        var run = Guid.NewGuid().ToString("N")[..8];
        var subject = new Seeded(Subject, $"subject{run}");
        var bystander = new Seeded(Bystander, $"bystander{run}");
        var shared = $"shared{run}";
        await SeedAsync(provider, subject, exporters);
        await SeedAsync(provider, bystander, exporters);
        await SeedSharedAsync(provider, subject, bystander, shared);

        var copy = await ExportAsync(provider, Subject);

        foreach (var section in copy.Sections)
        {
            var text = string.Join("\n", section.Records.Select(r => r.GetRawText()));
            text.Should().Contain(subject.Label, $"the {section.Store} section must hold the subject's records");
            text.Should().NotContain(bystander.Label, $"the {section.Store} section must hold nothing of another person's");
            text.Should().NotContain(shared, $"the {section.Store} section must hold no shared or global record");
        }

        var graphText = string.Join("\n", copy.Sections.Single(s => s.Store == "knowledge graph").Records.Select(r => r.GetRawText()));
        graphText.Should().Contain($"{subject.Label}-other-fact", "a quarantined memory is still the person's");
        graphText.Should().Contain($"{subject.Label}-edge", "the person's own edge");
        graphText.Should().NotContain($"{subject.Label}-link", "an unowned edge from shared knowledge is not the person's");
        string.Join("\n", copy.Sections.Single(s => s.Store == "conversations").Records.Select(r => r.GetRawText()))
            .Should().Contain($"{subject.Label} message", "the transcript, not only the conversation's header");

        copy.Sections.Select(s => s.Store).Should().BeEquivalentTo(
            copy.Manifest.Where(m => m.Status == ExportStatus.Included).Select(m => m.Store));
        copy.Manifest.Where(m => m.Status == ExportStatus.Failed).Should().BeEmpty();
    }

    [Fact]
    public async Task TheManifest_NamesEveryStoreErasureReachesThatTheCopyLeavesOut()
    {
        await using var provider = BuildProvider();

        var copy = await ExportAsync(provider, Subject);

        // Plans and spilled tool results hold the person's data but cannot yet be copied: they must be named, and the copy
        // must not read as complete while they are.
        copy.Manifest.Should().Contain(m => m.Store == "plans" && m.Status == ExportStatus.NotExportable);
        copy.Manifest.Should().Contain(m => m.Status == ExportStatus.RetainedByObligation || m.Status == ExportStatus.RetainedUnsearchable
            || m.Status == ExportStatus.RetainedUntilExpiry, "the logs and audit trails the host runs are listed too");
        copy.IsComplete.Should().BeFalse();
    }

    private static async Task SeedConversationAsync(IServiceProvider sp, Seeded p)
    {
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", p.OwnerId);
        await conversations.AppendMessageAsync(conversation.Id, p.OwnerId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"{p.Label} message", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Two owned nodes and an owned edge between them, plus a quarantined memory, which the person cannot read back
    /// through the product but which is still theirs.
    /// </summary>
    private static async Task SeedGraphAsync(IServiceProvider sp, Seeded p)
    {
        var graph = sp.GetRequiredService<IKnowledgeGraphStore>();
        await graph.AddNodesAsync(
        [
            Node($"{p.Label}-fact", p.OwnerId),
            Node($"{p.Label}-other-fact", p.OwnerId) with
            {
                Properties = new Dictionary<string, string> { ["trust"] = "untrusted" },
            },
        ]);
        await graph.AddEdgesAsync([Edge($"{p.Label}-edge", $"{p.Label}-fact", $"{p.Label}-other-fact", p.OwnerId)]);
    }

    /// <summary>
    /// A global node with no owner, an unowned edge from it to the subject's node (shared knowledge's, though erasure
    /// removes it with their node), and an edge the bystander owns into the subject's node (the bystander's). The global
    /// node's id carries no label; its content carries <paramref name="shared"/>.
    /// </summary>
    private static async Task SeedSharedAsync(ServiceProvider provider, Seeded subject, Seeded bystander, string shared)
    {
        await using var scope = provider.CreateAsyncScope();
        var graph = scope.ServiceProvider.GetRequiredService<IKnowledgeGraphStore>();
        var globalId = Guid.NewGuid().ToString("N");
        await graph.AddNodesAsync([Node(globalId, ownerId: null) with { Name = shared }]);
        await graph.AddEdgesAsync(
        [
            Edge($"{subject.Label}-link", globalId, $"{subject.Label}-fact", ownerId: null),
            Edge($"{bystander.Label}-reference", $"{bystander.Label}-fact", $"{subject.Label}-fact", bystander.OwnerId),
        ]);
    }

    private static GraphNode Node(string id, string? ownerId) =>
        new() { Id = id, Name = id, Type = "Memory", OwnerId = ownerId };

    private static GraphEdge Edge(string id, string source, string target, string? ownerId) =>
        new() { Id = id, SourceNodeId = source, TargetNodeId = target, Predicate = "relates_to", ChunkId = id, OwnerId = ownerId };

    private static ScheduleRecord Schedule(Seeded p) => new()
    {
        ScheduleId = $"schedule-{p.Label}",
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = p.OwnerId,
        TenantId = null,
        Envelope = new CapabilityEnvelope(),
        CronExpression = "*/30 * * * *",
        TimeZoneId = "UTC",
        Enabled = true,
        NextFireAt = DateTimeOffset.UtcNow.AddHours(1),
        CreatedAt = DateTimeOffset.UtcNow,
        Version = 0,
    };

    private static async Task SeedAsync(ServiceProvider provider, Seeded person, IEnumerable<string> exporters)
    {
        await using var scope = provider.CreateAsyncScope();
        foreach (var name in exporters)
            await Seeds[name](scope.ServiceProvider, person);
    }

    private static async Task<PersonalDataExport> ExportAsync(ServiceProvider provider, string ownerId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPersonalDataExport>().ExportOwnerAsync(ownerId, CancellationToken.None);
    }

    private static async Task<List<string>> ExportersTheCoordinatorReceivesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IPersonalDataExport>()
            .Should().BeOfType<PersonalDataExportCoordinator>().Subject;
        var exporters = (IReadOnlyList<IPersonalDataExporter>)typeof(PersonalDataExportCoordinator)
            .GetField("_exporters", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;
        return exporters.Select(e => e.GetType().Name).Distinct().ToList();
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
    {
        ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
        ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
        ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
        ["AppConfig:AI:Erasure:JournalDatabasePath"] = Path.Combine(_workingDir, "erasure-journal.db"),
        ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
    });
}
