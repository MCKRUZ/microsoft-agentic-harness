using System.Reflection;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Services.Compliance;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Planner;
using Domain.AI.RAG.Models;
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
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class ExportReachesEveryStoreEndToEndTests : IDisposable
{
    // Not the erasure end-to-end test's ids: the keyword index is one in-memory database for the whole process, and an
    // erasure there deletes by owner, so a shared id would let that test delete this one's seed while both run.
    private const string Subject = "Export-Subject-Oid-9C2E";
    private const string Bystander = "export-bystander-oid-1";

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

    private Dictionary<string, Func<IServiceProvider, Seeded, Task>> Seeds => new(StringComparer.Ordinal)
    {
        ["EfCoreConversationStore"] = SeedConversationAsync,
        ["FileSystemConversationStore"] = SeedConversationAsync,
        ["EfScheduleStore"] = (sp, p) => sp.GetRequiredService<IScheduleStore>().CreateAsync(Schedule(p), CancellationToken.None),
        ["InMemoryRunJobStore"] = (sp, p) =>
        {
            sp.GetRequiredService<IRunJobStore>().TryCreate(Run(p), int.MaxValue).Should().Be(RunAdmission.Accepted);
            return Task.CompletedTask;
        },
        // #990: a bundle run with the person's message, and the bundle they staged.
        ["InMemoryBundleRunJobStore"] = (sp, p) =>
        {
            BundleStoreSeeds.SeedFinishedRun(sp, p.OwnerId, p.Label);
            return Task.CompletedTask;
        },
        ["InMemoryBundleHandleStore"] = (sp, p) =>
        {
            BundleStoreSeeds.SeedStagedBundle(sp, _workingDir, p.OwnerId, p.Label);
            return Task.CompletedTask;
        },
        // #991: an evaluation run's submission names no owner; only its run record says it is the person's.
        ["EvalRunSubmissionExporter"] = (sp, p) =>
        {
            sp.GetRequiredService<IRunJobStore>().TryCreate(Run(p) with { JobId = $"eval-{p.Label}", Kind = RunKind.Evaluation },
                int.MaxValue).Should().Be(RunAdmission.Accepted);
            sp.GetRequiredService<IEvalRunSubmissionStore>().Add(new EvalRunSubmission
            {
                JobId = $"eval-{p.Label}",
                DatasetNames = [$"{p.Label} dataset"],
                Options = new EvalRunOptions(),
            });
            return Task.CompletedTask;
        },
        ["KnowledgeGraphExporter"] = SeedGraphAsync,
        ["FileSystemToolResultStore"] = SeedConversationSpillAsync,
        ["EfCorePlanStateStore"] = SeedPlannedTaskRunAsync,
        ["FaissVectorStore"] = (sp, p) => sp.GetRequiredService<IVectorStore>().IndexAsync([Chunk(p, "vector")]),
        ["SqliteFts5Store"] = (sp, p) => sp.GetRequiredService<IBm25Store>().IndexAsync([Chunk(p, "keyword")]),
        ["CrossSessionMemoryStore"] = (sp, p) => sp.GetRequiredService<ICrossSessionMemoryStore>().RememberAsync(new MemoryRecord
        {
            Id = $"{p.Label}-memory",
            Content = $"{p.Label} remembered preference",
            Source = "session",
            Weight = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = 0,
            OwnerId = p.OwnerId,
        }),
    };

    private static readonly float[] Embedding = [0.1f, 0.2f, 0.3f];

    /// <summary>A document chunk the person ingested, stamped with them as owner the way ingest stamps it.</summary>
    private static DocumentChunk Chunk(Seeded p, string index) => new()
    {
        Id = $"{p.Label}-{index}-chunk",
        DocumentId = $"{p.Label}-document",
        SectionPath = "Notes",
        Content = $"{p.Label} {index} chunk text",
        Tokens = 8,
        Embedding = Embedding,
        Metadata = new ChunkMetadata
        {
            SourceUri = new Uri($"file:///docs/{p.Label}.md"),
            CreatedAt = DateTimeOffset.UtcNow,
            SiblingChunkIds = [],
            OwnerId = p.OwnerId,
        },
    };

    [Fact]
    public async Task TheCopy_HoldsEveryRecordOfTheSubjectAndNothingOfAnyoneElse()
    {
        await using var provider = BuildProvider();
        var exporters = await ExportersTheCoordinatorReceivesAsync(provider);
        exporters.Where(name => !Seeds.ContainsKey(name)).Should().BeEmpty(
            "every store the export reads needs a seed here; add an entry for the new store");
        exporters.Should().Contain(["EfCoreConversationStore", "EfScheduleStore", "KnowledgeGraphExporter",
            "FileSystemToolResultStore", "EfCorePlanStateStore", "FaissVectorStore", "SqliteFts5Store", "InMemoryRunJobStore",
            "InMemoryBundleRunJobStore", "InMemoryBundleHandleStore", "EvalRunSubmissionExporter"],
            "a floor, so a composition that reads fewer stores than this one configures cannot pass");

        // Letters only: stores redact on write, and a run of digits in a label reads as a phone number. A redacted label
        // would also hide a leaked record of the bystander's from the check below.
        var run = new string(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
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

        // #906: a planned task's model step ran as a conversation of its own and its tool steps under the run's id. Only the
        // plan store's record of the run leads to them, so these prove the copy reads the same ids erasure does.
        var spillText = string.Join("\n", copy.Sections.Single(s => s.Store == "spilled tool results").Records.Select(r => r.GetRawText()));
        spillText.Should().Contain($"{subject.Label} conversation spill");
        spillText.Should().Contain($"{subject.Label} model step spill", "reached only through the run's step conversation");
        spillText.Should().Contain($"{subject.Label} tool step spill", "reached only through the run's tool-result scope");
        var planText = string.Join("\n", copy.Sections.Single(s => s.Store == "plans").Records.Select(r => r.GetRawText()));
        planText.Should().Contain($"plan {subject.Label}").And.Contain($"job{subject.Label}", "the plan with its run record");

        copy.Sections.Select(s => s.Store).Should().BeEquivalentTo(
            copy.Manifest.Where(m => m.Status == ExportStatus.Included).Select(m => m.Store));
        copy.Manifest.Where(m => m.Status == ExportStatus.Failed).Should().BeEmpty();
    }

    [Fact]
    public async Task TheManifest_NamesNoStoreAsUncopyable_AndListsTheRetainedOnes()
    {
        await using var provider = BuildProvider();

        var copy = await ExportAsync(provider, Subject);

        // Every store this composition erases a person from now returns a copy, so none is named as one that cannot.
        copy.Manifest.Should().NotContain(m => m.Status == ExportStatus.NotExportable,
            "every store that can erase one person's data can also copy it");
        copy.Manifest.Should().Contain(m => m.Status == ExportStatus.RetainedByObligation || m.Status == ExportStatus.RetainedUnsearchable
            || m.Status == ExportStatus.RetainedUntilExpiry, "the logs and audit trails the host runs are listed too");
    }

    private static async Task SeedConversationAsync(IServiceProvider sp, Seeded p)
    {
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", p.OwnerId);
        await conversations.AppendMessageAsync(conversation.Id, p.OwnerId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"{p.Label} message", DateTimeOffset.UtcNow));
    }

    /// <summary>Tool output spilled under one of the person's own conversations.</summary>
    private static async Task SeedConversationSpillAsync(IServiceProvider sp, Seeded p)
    {
        var conversation = await sp.GetRequiredService<IConversationStore>().CreateAsync("agent", p.OwnerId);
        await SpillAsync(sp, conversation.Id, $"{p.Label} conversation spill");
    }

    /// <summary>
    /// A planned-task run written the way the run writes it: the plan store records the run, a model step spills under its
    /// own conversation id and a tool step under the run's tool-result scope, then the run ends.
    /// </summary>
    private static async Task SeedPlannedTaskRunAsync(IServiceProvider sp, Seeded p)
    {
        var plan = Plan(p.Label);
        var runScope = plan.Id.Value.ToString();
        var toolResultScopeId = $"job{p.Label}";
        var plans = sp.GetRequiredService<IPlanStateStore>();
        using (sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: p.OwnerId))
        {
            (await plans.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await plans.RecordRunAsync(plan.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await SpillAsync(sp, PlanRunKeys.StepConversationId(runScope, plan.Steps[1].Id), $"{p.Label} model step spill");
        await SpillAsync(sp, toolResultScopeId, $"{p.Label} tool step spill");

        using (sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: p.OwnerId))
            (await plans.RecordRunEndedAsync(plan.Id, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    private static async Task SpillAsync(IServiceProvider sp, string scopeId, string text)
    {
        // Ordinary words to pass the spill threshold: a long run of one character is sanitized as an injection attempt.
        var content = text + string.Concat(Enumerable.Repeat(" and the search returned this row", 8));
        (await sp.GetRequiredService<IToolResultStore>().StoreIfLargeAsync(scopeId, "search", null, content, scopeIsRetrievable: true))
            .IsPersistedToDisk.Should().BeTrue("the seed must actually spill");
    }

    private static PlanGraph Plan(string label)
    {
        var steps = Enumerable.Range(0, 2).Select(i => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"Step {i}",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig { SystemPrompt = $"Prompt {i}", ModelDeploymentKey = "gpt-4o" },
            RetryPolicy = new RetryPolicy { MaxRetries = 2 },
            Timeout = TimeSpan.FromSeconds(30),
        }).ToList();
        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = $"plan {label}",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
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

    private static RunRecord Run(Seeded p) => new()
    {
        JobId = $"run-{p.Label}",
        Kind = RunKind.Workflow,
        TargetId = $"target-{p.Label}",
        OwnerId = p.OwnerId,
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

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

    private async Task SeedAsync(ServiceProvider provider, Seeded person, IEnumerable<string> exporters)
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
        ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "100",
        ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
    });
}
