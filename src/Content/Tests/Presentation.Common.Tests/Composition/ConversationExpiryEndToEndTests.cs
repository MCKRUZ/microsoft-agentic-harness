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
/// Conversation expiry (#901) through the real composition: a conversation idle past the period goes with everything found
/// through it, while the same person's recent conversation and everything they own outside conversations stays.
/// </summary>
/// <remarks>
/// Exhaustive by construction, as <see cref="ErasureReachesEveryStoreEndToEndTests"/> is: every conversation-scoped store
/// the expiry receives in this composition must have a seed and a count in <see cref="ConversationStores"/>. The
/// owner-wide stores are seeded too, as the control: expiry must not touch them.
/// </remarks>
public sealed class ConversationExpiryEndToEndTests : IDisposable
{
    private const string Owner = "Owner-Oid-51C2";
    private static readonly TimeSpan Year = TimeSpan.FromDays(365);
    private static readonly float[] Embedding = Enumerable.Range(0, 8).Select(i => (float)(i + 1)).ToArray();

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "conversation-expiry-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    private sealed record StoreProof(Func<IServiceProvider, string, Task> Seed, Func<IServiceProvider, string, Task<int>> Remaining);

    /// <summary>Stores reached through one conversation, keyed by type name: seeded and counted per conversation id.</summary>
    private Dictionary<string, StoreProof> ConversationStores => new(StringComparer.Ordinal)
    {
        ["EfCoreConversationStore"] = new(
            (_, _) => Task.CompletedTask, // written by SeedConversationAsync
            async (sp, id) => await sp.GetRequiredService<IConversationStore>().GetAsync(id, Owner) is null ? 0 : 1),
        ["SqliteConversationBudgetTracker"] = new(
            (sp, id) => sp.GetRequiredService<IConversationBudgetTracker>().RecordUsageAsync(id, 100),
            async (sp, id) => (await sp.GetRequiredService<IConversationBudgetTracker>().GetStatusAsync(id)).ConsumedTokens),
        ["FileSystemToolResultStore"] = new(
            async (sp, id) => (await sp.GetRequiredService<IToolResultStore>().StoreIfLargeAsync(
                id, "search", null, new string('x', 500), scopeIsRetrievable: true))
                .IsPersistedToDisk.Should().BeTrue("the seed must actually spill"),
            (_, id) => Task.FromResult(SpilledFiles(id))),
        ["EfCorePromptUsageStore"] = new(
            (sp, id) => sp.GetRequiredService<IPromptUsageStore>().AppendAsync(PromptUsage($"{id}:1"), CancellationToken.None),
            async (sp, id) =>
            {
                await using var db = await sp.GetRequiredService<IDbContextFactory<PromptUsageDbContext>>().CreateDbContextAsync();
                return await db.PromptUsages.CountAsync(u => u.CaseId == $"{id}:1");
            }),
    };

    [Fact]
    public async Task AnIdleConversation_GoesWithWhatIsFoundThroughIt_AndNothingElseOfThePersonsGoes()
    {
        await using var provider = BuildProvider();

        var reached = await ConversationScopedStoresAsync(provider);
        reached.Where(name => !ConversationStores.ContainsKey(name)).Should().BeEmpty(
            "every conversation-scoped store expiry reaches needs a seed and a count here; add an entry for the new store");
        reached.Should().Contain(["EfCoreConversationStore", "SqliteConversationBudgetTracker", "FileSystemToolResultStore",
            "EfCorePromptUsageStore"], "a floor, so a composition reaching fewer stores cannot pass");

        var old = await SeedConversationAsync(provider, reached, "old");
        var recent = await SeedConversationAsync(provider, reached, "recent");
        await SeedOwnerWideAsync(provider);
        await BackdateAsync(provider, old, Year + TimeSpan.FromDays(30));
        foreach (var name in reached)
            (await RemainingAsync(provider, name, old)).Should().BeGreaterThan(0, $"{name} must hold the seed before expiry");

        int expired;
        await using (var scope = provider.CreateAsyncScope())
            expired = await scope.ServiceProvider.GetRequiredService<IConversationExpiry>().ExpireIdleAsync(Year, 100, CancellationToken.None);

        expired.Should().Be(1);
        foreach (var name in reached)
        {
            (await RemainingAsync(provider, name, old)).Should().Be(0, $"{name} must keep nothing of the expired conversation");
            (await RemainingAsync(provider, name, recent)).Should().BeGreaterThan(0, $"{name} must keep the conversation still in use");
        }

        await AssertOwnerWideDataKeptAsync(provider);
    }

    [Fact]
    public async Task ExpiredText_IsNotLeftInTheDatabaseFilesOnDisk()
    {
        await using var provider = BuildProvider();
        var reached = await ConversationScopedStoresAsync(provider);
        var run = Guid.NewGuid().ToString("N")[..8];
        var old = await SeedConversationAsync(provider, reached, $"expiredmark{run}");
        await SeedConversationAsync(provider, reached, $"keptmark{run}");
        await BackdateAsync(provider, old, Year + TimeSpan.FromDays(1));

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IConversationExpiry>().ExpireIdleAsync(Year, 100, CancellationToken.None);

        // Scanned with the pooled connections still open, as in a running host; the kept text is the control.
        var files = Directory.EnumerateFiles(_workingDir, "*.db*", SearchOption.TopDirectoryOnly).ToList();
        FilesContaining(files, $"keptmark{run}").Should().NotBeEmpty("the scan must see live text, or it cannot see expired text");
        string.Join(", ", FilesContaining(files, $"expiredmark{run}")).Should().BeEmpty(
            "expired text must not survive in free pages or the write-ahead log");
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
    {
        ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
        ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
        ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
        ["AppConfig:AI:PromptUsage:PersistenceEnabled"] = "true",
        ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
        ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "100",
        ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
    });

    /// <summary>The conversation-scoped stores the expiry actually received in this composition, by type name.</summary>
    private static async Task<List<string>> ConversationScopedStoresAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var expiry = scope.ServiceProvider.GetRequiredService<IConversationExpiry>()
            .Should().BeOfType<ConversationExpiry>().Subject;
        var erasers = (IReadOnlyList<IPersonalDataEraser>)typeof(ConversationExpiry)
            .GetField("_erasers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(expiry)!;
        return erasers.Select(e => e.GetType().Name).Distinct().ToList();
    }

    private async Task<string> SeedConversationAsync(ServiceProvider provider, IReadOnlyList<string> reached, string label)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var conversations = sp.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", Owner);
        await conversations.AppendMessageAsync(conversation.Id, Owner,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"{label} message", DateTimeOffset.UtcNow));
        foreach (var name in reached)
            await ConversationStores[name].Seed(sp, conversation.Id);
        return conversation.Id;
    }

    /// <summary>Owner-wide records expiry must leave alone: a schedule, a plan and an ingested document.</summary>
    private static async Task SeedOwnerWideAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IScheduleStore>().CreateAsync(Schedule(), CancellationToken.None);
        using (sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: Owner))
            (await sp.GetRequiredService<IPlanStateStore>().SavePlanAsync(Plan(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await sp.GetRequiredService<IBm25Store>().IndexAsync([Chunk()]);
    }

    private static async Task AssertOwnerWideDataKeptAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        (await sp.GetRequiredService<IScheduleStore>().ListForOwnerAsync(Owner, null, CancellationToken.None))
            .Should().ContainSingle("a schedule belongs to the person, not to a conversation");
        await using var planner = await sp.GetRequiredService<IDbContextFactory<PlannerDbContext>>().CreateDbContextAsync();
        (await planner.PlanGraphs.CountAsync()).Should().Be(1, "a plan belongs to the person, not to a conversation");
        (await sp.GetRequiredService<IBm25Store>().SearchAsync(ChunkKeyword, 50)).Should().Contain(
            r => r.Chunk.Id == ChunkId, "an ingested document belongs to the person, not to a conversation");
    }

    /// <summary>Moves a conversation's last activity into the past, as if nobody had written to it since.</summary>
    private static async Task BackdateAsync(ServiceProvider provider, string conversationId, TimeSpan age)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ConversationDbContext>>().CreateDbContextAsync();
        var lastActivity = DateTimeOffset.UtcNow - age;
        (await db.Conversations.Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, lastActivity))).Should().Be(1);
    }

    private async Task<int> RemainingAsync(ServiceProvider provider, string store, string conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await ConversationStores[store].Remaining(scope.ServiceProvider, conversationId);
    }

    private int SpilledFiles(string conversationId)
    {
        var directory = Path.Combine(_workingDir, "spills", conversationId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count() : 0;
    }

    private static List<string> FilesContaining(IEnumerable<string> files, string text)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        return files.Where(f => ReadShared(f).AsSpan().IndexOf(needle) >= 0).Select(Path.GetFileName).ToList()!;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static readonly string ChunkKeyword = "expirycontrol" + Guid.NewGuid().ToString("N")[..8];
    private static string ChunkId => $"{ChunkKeyword}_chunk_0";

    private static DocumentChunk Chunk() => new()
    {
        Id = ChunkId,
        DocumentId = $"{ChunkKeyword}-document",
        SectionPath = "Notes",
        Content = $"{ChunkKeyword} appears in this chunk",
        Tokens = 8,
        Embedding = Embedding,
        Metadata = new ChunkMetadata
        {
            SourceUri = new Uri($"file:///docs/{ChunkKeyword}.md"),
            CreatedAt = DateTimeOffset.UtcNow,
            SiblingChunkIds = [],
            OwnerId = Owner,
        },
    };

    private static PlanGraph Plan()
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
            Name = "owner plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration { MaxParallelSteps = 4, PlanTimeout = TimeSpan.FromMinutes(10) },
        };
    }

    private static ScheduleRecord Schedule() => new()
    {
        ScheduleId = "schedule-expiry-control",
        Kind = RunKind.Workflow,
        TargetId = Guid.NewGuid().ToString(),
        OwnerId = Owner,
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
