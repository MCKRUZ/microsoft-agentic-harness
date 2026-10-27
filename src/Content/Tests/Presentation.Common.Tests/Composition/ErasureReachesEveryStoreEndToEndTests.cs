using System.Reflection;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Application.AI.Common.Services.Compliance;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Planner;
using Domain.AI.Prompts;
using Domain.AI.RAG.Models;
using Domain.AI.Runs;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Prompts.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// #900's done-when, through the real composition: seed every store the erasure coordinator reaches for two people, erase
/// one, and nothing of theirs remains while everything of the other person's survives.
/// </summary>
/// <remarks>
/// <para>
/// Exhaustive by construction. <see cref="Stores"/> holds a seed and a count for each eraser type, and the test first
/// requires every eraser the coordinator actually receives in this composition to have an entry. A store added later
/// without one fails here, rather than being silently left out of the proof.
/// </para>
/// <para>
/// The knowledge graph is erased through the orchestrator rather than an eraser, so it has its own seed and check. The
/// document indexes run as the AgentHub host ships them (FAISS for vectors, SQLite FTS5 for keywords), with a fixed
/// embedding so no model is needed.
/// </para>
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class ErasureReachesEveryStoreEndToEndTests : IDisposable
{
    private const string Subject = "Subject-Oid-7F3A";
    private const string Bystander = "bystander-oid-1";
    private static readonly float[] Embedding = Enumerable.Range(0, 8).Select(i => (float)(i + 1)).ToArray();

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "erasure-every-store-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    /// <summary>A person's seeded records, so each check can find exactly what was written for them.</summary>
    private sealed record Seeded(string OwnerId, string ConversationId, PlanId PlanId, string Label);

    private sealed record StoreProof(
        Func<IServiceProvider, Seeded, Task> Seed,
        Func<IServiceProvider, Seeded, Task<int>> Remaining);

    private Dictionary<string, StoreProof> Stores => new(StringComparer.Ordinal)
    {
        ["EfCoreConversationStore"] = new(
            (_, _) => Task.CompletedTask, // written by SeedConversationAsync for every person
            async (sp, p) => (await sp.GetRequiredService<IConversationStore>().ListAsync(p.OwnerId)).Count),
        ["SqliteConversationBudgetTracker"] = new(
            (sp, p) => sp.GetRequiredService<IConversationBudgetTracker>().RecordUsageAsync(p.ConversationId, 100),
            async (sp, p) => (await sp.GetRequiredService<IConversationBudgetTracker>().GetStatusAsync(p.ConversationId)).ConsumedTokens),
        ["InProcessConversationBudgetTracker"] = new(
            (sp, p) => sp.GetRequiredService<IConversationBudgetTracker>().RecordUsageAsync(p.ConversationId, 100),
            async (sp, p) => (await sp.GetRequiredService<IConversationBudgetTracker>().GetStatusAsync(p.ConversationId)).ConsumedTokens),
        ["FileSystemToolResultStore"] = new(
            async (sp, p) => (await sp.GetRequiredService<IToolResultStore>().StoreIfLargeAsync(
                p.ConversationId, "search", null, new string('x', 500), scopeIsRetrievable: true))
                .IsPersistedToDisk.Should().BeTrue("the seed must actually spill"),
            (_, p) => Task.FromResult(SpilledFiles(p.ConversationId))),
        ["EfCorePlanStateStore"] = new(
            async (sp, p) =>
            {
                using var caller = sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: p.OwnerId);
                (await sp.GetRequiredService<IPlanStateStore>().SavePlanAsync(Plan(p.PlanId, p.Label), CancellationToken.None))
                    .IsSuccess.Should().BeTrue();
            },
            async (sp, p) =>
            {
                await using var db = await sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContextAsync();
                return await db.PlanGraphs.CountAsync(g => g.Id == p.PlanId.Value)
                    + await db.PlanSteps.CountAsync(s => s.PlanGraphId == p.PlanId.Value);
            }),
        ["EfScheduleStore"] = new(
            (sp, p) => sp.GetRequiredService<IScheduleStore>().CreateAsync(Schedule(p), CancellationToken.None),
            async (sp, p) => (await sp.GetRequiredService<IScheduleStore>().ListForOwnerAsync(p.OwnerId, null, CancellationToken.None)).Count),
        ["EfCorePromptUsageStore"] = new(
            (sp, p) => sp.GetRequiredService<IPromptUsageStore>().AppendAsync(PromptUsage($"{p.ConversationId}:1"), CancellationToken.None),
            async (sp, p) =>
            {
                await using var db = await sp.GetRequiredService<IDbContextFactory<PromptUsageDbContext>>().CreateDbContextAsync();
                return await db.PromptUsages.CountAsync(u => u.CaseId == $"{p.ConversationId}:1");
            }),
        ["FaissVectorStore"] = new(
            (sp, p) => sp.GetRequiredService<IVectorStore>().IndexAsync([Chunk(p)]),
            async (sp, p) => (await sp.GetRequiredService<IVectorStore>().SearchAsync(Embedding, 50))
                .Count(r => r.Chunk.Id == ChunkId(p))),
        ["SqliteFts5Store"] = new(
            (sp, p) => sp.GetRequiredService<IBm25Store>().IndexAsync([Chunk(p)]),
            async (sp, p) => (await sp.GetRequiredService<IBm25Store>().SearchAsync($"{p.Label}keyword", 50))
                .Count(r => r.Chunk.Id == ChunkId(p))),
    };

    [Fact]
    public async Task EveryStoreTheCoordinatorReaches_IsEmptiedForTheSubjectAndKeptForEveryoneElse()
    {
        await using var provider = BuildProvider();

        var reached = await ErasersTheCoordinatorReceivesAsync(provider);
        reached.Where(name => !Stores.ContainsKey(name)).Should().BeEmpty(
            "every store the coordinator reaches needs a seed and a check here; add an entry for the new store");
        reached.Should().Contain(["EfCoreConversationStore", "FileSystemToolResultStore", "EfCorePlanStateStore", "EfScheduleStore",
            "EfCorePromptUsageStore", "FaissVectorStore", "SqliteFts5Store"],
            "a floor, so a composition that reaches fewer stores than this one configures cannot pass");

        // Labels unique to this run: the keyword index is a process-wide in-memory database other tests also write to.
        var run = Guid.NewGuid().ToString("N")[..8];
        var subject = await SeedPersonAsync(provider, Subject, $"subject{run}", reached);
        var bystander = await SeedPersonAsync(provider, Bystander, $"bystander{run}", reached);
        foreach (var name in reached)
            (await RemainingAsync(provider, name, subject)).Should().BeGreaterThan(0, $"{name} must hold the seed before erasing");

        ErasureReceipt receipt;
        await using (var scope = provider.CreateAsyncScope())
            receipt = await scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>().EraseOwnerAsync(Subject, CancellationToken.None);

        receipt.Stores.Should().NotContain(s => s.Status == Domain.Common.Compliance.StoreErasureStatus.Failed
            || s.Status == Domain.Common.Compliance.StoreErasureStatus.Skipped);
        foreach (var name in reached)
        {
            (await RemainingAsync(provider, name, subject)).Should().Be(0, $"{name} must keep nothing of the subject's");
            (await RemainingAsync(provider, name, bystander)).Should().BeGreaterThan(0, $"{name} must keep everyone else's");
        }

        await using var check = provider.CreateAsyncScope();
        var graph = check.ServiceProvider.GetRequiredService<IKnowledgeGraphStore>();
        (await graph.GetNodesByOwnerAsync(Subject.ToLowerInvariant())).Should().BeEmpty();
        (await graph.GetNodesByOwnerAsync(Bystander)).Should().ContainSingle();
    }

    [Fact]
    public async Task PlannedTaskRunRecords_AreErasedWithThePersonWhoRanThem_AndEveryoneElsesAreKept()
    {
        // #906. A planned task's model steps each run as their own conversation, "{runScope}:{stepId}", and its tool steps
        // spill under the run's own id; none of these is one of the person's conversations. Each person runs their own
        // workflow, so the run scope is the plan id, as WorkflowRunKindExecutor arms it.
        await using var provider = BuildProvider();
        var run = Guid.NewGuid().ToString("N")[..8];
        var subject = await SeedPlannedTaskRunAsync(provider, Subject, $"subjectrun{run}");
        var bystander = await SeedPlannedTaskRunAsync(provider, Bystander, $"bystanderrun{run}");
        (await PlannedTaskRecordsAsync(provider, subject)).Should().AllSatisfy(r => r.Value.Should().BeGreaterThan(0,
            $"{r.Key} must hold the seed before erasing"));

        ErasureReceipt receipt;
        await using (var scope = provider.CreateAsyncScope())
            receipt = await scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>().EraseOwnerAsync(Subject, CancellationToken.None);

        receipt.Stores.Should().NotContain(s => s.Status == Domain.Common.Compliance.StoreErasureStatus.Failed
            || s.Status == Domain.Common.Compliance.StoreErasureStatus.Skipped);
        (await PlannedTaskRecordsAsync(provider, subject)).Should().AllSatisfy(r => r.Value.Should().Be(0,
            $"{r.Key} must keep nothing of the subject's planned task"));
        (await PlannedTaskRecordsAsync(provider, bystander)).Should().AllSatisfy(r => r.Value.Should().BeGreaterThan(0,
            $"{r.Key} must keep everyone else's"));
    }

    /// <summary>A person's planned-task run: their plan, a model step's conversation id and the run's tool-result scope.</summary>
    private sealed record SeededRun(Seeded Person, string StepConversationId, string RunBudgetKey, string ToolResultScopeId);

    /// <summary>
    /// What a workflow run leaves behind, written the way the run writes it: the plan store records the run before any
    /// step runs, then a model step's turn spends budget, spills and is recorded for prompt usage under its own
    /// conversation id, and a tool step spills under the run's id.
    /// </summary>
    private static async Task<SeededRun> SeedPlannedTaskRunAsync(ServiceProvider provider, string ownerId, string label)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var person = new Seeded(ownerId, "", PlanId.New(), label);
        var plan = Plan(person.PlanId, label);
        var runScope = person.PlanId.Value.ToString();
        var toolResultScopeId = $"job{label}";

        using (sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: ownerId))
        {
            var plans = sp.GetRequiredService<IPlanStateStore>();
            (await plans.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await plans.RecordRunAsync(person.PlanId, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        // The run's steps write next, and it ends before erasure; one still in progress would keep its plan by design.

        var stepConversationId = PlanRunKeys.StepConversationId(runScope, plan.Steps[1].Id);
        var runBudgetKey = PlanRunKeys.RunBudgetKey(runScope);
        var budget = sp.GetRequiredService<IConversationBudgetTracker>();
        await budget.RecordUsageAsync(stepConversationId, 100);
        await budget.RecordUsageAsync(runBudgetKey, 100);
        var spills = sp.GetRequiredService<IToolResultStore>();
        foreach (var scopeId in new[] { stepConversationId, toolResultScopeId })
        {
            (await spills.StoreIfLargeAsync(scopeId, "search", null, new string('x', 500), scopeIsRetrievable: true))
                .IsPersistedToDisk.Should().BeTrue("the seed must actually spill");
        }

        await sp.GetRequiredService<IPromptUsageStore>().AppendAsync(PromptUsage($"{stepConversationId}:1"), CancellationToken.None);
        using (sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: ownerId))
        {
            (await sp.GetRequiredService<IPlanStateStore>().RecordRunEndedAsync(
                person.PlanId, runScope, toolResultScopeId, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        return new SeededRun(person, stepConversationId, runBudgetKey, toolResultScopeId);
    }

    private async Task<Dictionary<string, int>> PlannedTaskRecordsAsync(ServiceProvider provider, SeededRun run)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var budget = sp.GetRequiredService<IConversationBudgetTracker>();
        await using var usage = await sp.GetRequiredService<IDbContextFactory<PromptUsageDbContext>>().CreateDbContextAsync();
        await using var planner = await sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContextAsync();
        return new Dictionary<string, int>
        {
            ["step budget"] = (await budget.GetStatusAsync(run.StepConversationId)).ConsumedTokens,
            ["run budget"] = (await budget.GetStatusAsync(run.RunBudgetKey)).ConsumedTokens,
            ["model step spills"] = SpilledFiles(run.StepConversationId.Replace(':', '~')),
            ["tool step spills"] = SpilledFiles(run.ToolResultScopeId),
            ["prompt usage"] = await usage.PromptUsages.CountAsync(u => u.CaseId == $"{run.StepConversationId}:1"),
            ["run records"] = await planner.PlanRuns.CountAsync(r => r.PlanGraphId == run.Person.PlanId.Value),
        };
    }

    [Fact]
    public async Task ErasedText_IsNotLeftInTheDatabaseFilesOnDisk()
    {
        // SQLite marks deleted rows' pages free without overwriting them, and a write-ahead log keeps the frames that wrote
        // them, so a "deleted" transcript can still be read from the file. The bystander's text is the control: the scan
        // must find it, or it proves nothing about the subject's.
        await using var provider = BuildProvider();
        var reached = await ErasersTheCoordinatorReceivesAsync(provider);
        var run = Guid.NewGuid().ToString("N")[..8];
        var subject = await SeedPersonAsync(provider, Subject, $"subjectmark{run}", reached);
        var bystander = await SeedPersonAsync(provider, Bystander, $"bystandermark{run}", reached);

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>().EraseOwnerAsync(Subject, CancellationToken.None);

        // Scanned while the pooled connections are still open, as in a running host: closing the last connection would
        // checkpoint and delete each write-ahead log, which is the state after shutdown, not the one an erasure must reach.
        var files = Directory.EnumerateFiles(_workingDir, "*.db*", SearchOption.TopDirectoryOnly).ToList();
        files.Should().Contain(f => f.EndsWith("conversations.db-wal", StringComparison.Ordinal),
            "the databases run in write-ahead-log mode, the case where erased bytes can sit in the log");
        FilesContaining(files, bystander.Label).Should().NotBeEmpty("the scan must see live text, or it cannot see erased text");
        string.Join(", ", FilesContaining(files, subject.Label)).Should().BeEmpty(
            "erased text must not survive in free pages or the write-ahead log (scanned: {0})",
            string.Join(", ", files.Select(Path.GetFileName)));
    }

    private static List<string> FilesContaining(IEnumerable<string> files, string text)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        return files.Where(f => ReadShared(f).AsSpan().IndexOf(needle) >= 0).Select(Path.GetFileName).ToList()!;
    }

    /// <summary>Reads a file the host still has open, without asking for exclusive access.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
    {
        ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
        ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
        ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
        // In the working folder so the byte scan covers it: a cleared journal must not leave the person's ids on disk.
        ["AppConfig:AI:Erasure:JournalDatabasePath"] = Path.Combine(_workingDir, "erasure-journal.db"),
        // Prompt-usage persistence is off by default; on here so its store is among those proven.
        ["AppConfig:AI:PromptUsage:PersistenceEnabled"] = "true",
        ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
        ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "100",
        ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
    });

    private static async Task<List<string>> ErasersTheCoordinatorReceivesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>()
            .Should().BeOfType<PersonalDataErasureCoordinator>().Subject;
        var erasers = (IReadOnlyList<IPersonalDataEraser>)typeof(PersonalDataErasureCoordinator)
            .GetField("_erasers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;
        return erasers.Where(e => e is not NonErasableStore).Select(e => e.GetType().Name).Distinct().ToList();
    }

    private async Task<Seeded> SeedPersonAsync(ServiceProvider provider, string ownerId, string label, IReadOnlyList<string> reached)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var conversations = sp.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", ownerId);
        await conversations.AppendMessageAsync(conversation.Id, ownerId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"{label} message", DateTimeOffset.UtcNow));

        var seeded = new Seeded(ownerId, conversation.Id, PlanId.New(), label);
        foreach (var name in reached)
            await Stores[name].Seed(sp, seeded);

        await sp.GetRequiredService<IKnowledgeGraphStore>().AddNodesAsync(
            [new GraphNode { Id = $"memory:default:{ownerId.ToLowerInvariant()}:{label}", Name = label, Type = "Memory", OwnerId = ownerId }]);
        return seeded;
    }

    private async Task<int> RemainingAsync(ServiceProvider provider, string store, Seeded person)
    {
        await using var scope = provider.CreateAsyncScope();
        return await Stores[store].Remaining(scope.ServiceProvider, person);
    }

    private int SpilledFiles(string scopeDirectory)
    {
        var directory = Path.Combine(_workingDir, "spills", scopeDirectory);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count() : 0;
    }

    private static string ChunkId(Seeded p) => $"{p.Label}_chunk_0";

    private static DocumentChunk Chunk(Seeded p) => new()
    {
        Id = ChunkId(p),
        DocumentId = $"{p.Label}-document",
        SectionPath = "Notes",
        Content = $"{p.Label}keyword appears in this chunk",
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

    private static PlanGraph Plan(PlanId id, string label)
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
            Id = id,
            Name = $"plan {label}",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

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

    private static PromptUsageRecord PromptUsage(string caseId) => new()
    {
        Descriptor = new PromptDescriptor { Name = "p", Version = new PromptVersion(1, 0), ContentHash = "deadbeef", Body = "body" },
        CaseId = caseId,
        MetricKey = "m",
        TraceId = "trace-1",
        SpanId = "span-1",
        RecordedAtUtc = DateTimeOffset.UtcNow,
    };
}
