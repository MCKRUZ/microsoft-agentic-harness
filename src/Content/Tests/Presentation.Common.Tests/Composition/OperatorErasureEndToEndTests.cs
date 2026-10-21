using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Models.Conversations;
using Application.Core.Compliance;
using Application.Core.CQRS.Compliance.EraseSubjectData;
using Domain.AI.Bundles;
using Domain.AI.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.Runs;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Operator-initiated erasure through the real composition (#900): an operator erases another person while their own
/// scope is ambient, and everything of that person's is gone, everything of anyone else's survives, and no log line or
/// audit record names the person erased.
/// </summary>
/// <remarks>
/// <para>
/// Graph isolation is switched on, because that is the case the operator path breaks without system-scoped erasure:
/// the tenant-isolated graph answers by the caller, so under the operator's scope it would find none of the subject's
/// memories and the receipt would still read <c>Full</c>.
/// </para>
/// <para>
/// Seeds the stores the default composition erases through the conversation id (transcript, token budget, tool-result
/// spills), the schedules, and the knowledge graph. Each store's own two-owner behaviour, plus the stores off by default
/// (plans' per-step records, prompt usage, the call-once ledger, document indexes, the observability database), is
/// pinned by that store's own <c>*ErasureTests</c>; this test proves they are reached, under an operator, without leaking.
/// </para>
/// </remarks>
public sealed class OperatorErasureEndToEndTests : IDisposable
{
    // Mixed case on purpose: conversations match the id as issued, the graph and schedules match it canonicalized.
    private const string Subject = "Subject-Oid-7F3A";
    private const string Bystander = "bystander-oid-1";
    private const string Operator = "dpo-operator-9";
    private const string Marker = "subject-secret-transcript-marker";

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "operator-erasure-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ConcurrentQueue<MemoryAuditEvent> _memoryEvents = new();

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task OperatorErasesSubject_EverythingOfTheirsIsGoneAndNothingNamesThem()
    {
        await using var provider = BuildProvider();

        var subject = await SeedPersonAsync(provider, Subject, "subject");
        var bystander = await SeedPersonAsync(provider, Bystander, "bystander");

        var receipt = await EraseAsOperatorAsync(provider);

        receipt.Stores.Should().NotContain(s => s.Status == Domain.Common.Compliance.StoreErasureStatus.Failed);
        receipt.NodesDeleted.Should().BeGreaterThan(0, "the operator's own scope must not hide the subject's memories");

        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        (await sp.GetRequiredService<IConversationStore>().ListAsync(Subject)).Should().BeEmpty();
        (await sp.GetRequiredService<IConversationStore>().ListAsync(Bystander)).Should().ContainSingle();
        (await sp.GetRequiredService<IKnowledgeGraphStore>().GetNodesByOwnerAsync(Subject.ToLowerInvariant()))
            .Should().BeEmpty();
        (await sp.GetRequiredService<IKnowledgeGraphStore>().GetNodesByOwnerAsync(Bystander)).Should().ContainSingle();
        (await sp.GetRequiredService<IScheduleStore>().ListForOwnerAsync(Subject, tenantId: null, CancellationToken.None))
            .Should().BeEmpty();
        (await sp.GetRequiredService<IScheduleStore>().ListForOwnerAsync(Bystander, tenantId: null, CancellationToken.None))
            .Should().ContainSingle();
        Directory.EnumerateFiles(SpillDirectory(bystander.ConversationId), "*", SearchOption.AllDirectories)
            .Should().NotBeEmpty("another person's spilled tool results must survive");
        SpilledFiles(subject.ConversationId).Should().BeEmpty();

        var everything = EverythingRecorded().ToList();
        everything.Should().NotContain(t => t.Contains(Subject, StringComparison.OrdinalIgnoreCase),
            "no log line or audit record may keep the identity the erasure removed");
        everything.Should().NotContain(t => t.Contains(subject.NodeId, StringComparison.OrdinalIgnoreCase));
        everything.Should().NotContain(t => t.Contains(subject.ConversationId, StringComparison.OrdinalIgnoreCase));

        // The shipped audit sink writes the record: the attempt and the outcome, by the operator, carrying the pseudonymous
        // reference and the counts, not the bare action line.
        var auditLines = _logs.Messages.Where(m => m.Contains(AuditedErasure.AuditAction, StringComparison.Ordinal)).ToList();
        auditLines.Should().HaveCount(2).And.OnlyContain(m => m.Contains($"by {Operator}", StringComparison.Ordinal));
        auditLines.Should().ContainSingle(m => m.Contains("Attempted") && m.Contains("phase=attempt"));
        auditLines.Should().ContainSingle(m => m.Contains("phase=outcome") && m.Contains("completeness=")
            && m.Contains("store.conversations=Erased:1"));
        auditLines.Should().OnlyContain(m => m.Contains("subject_reference=", StringComparison.Ordinal));
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(
        new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
            ["AppConfig:AI:ContextManagement:ToolResultStorage:PerResultCharLimit"] = "100",
            ["AppConfig:AI:Rag:GraphRag:MultiTenantIsolation"] = "true",
            // As AgentHub, the host that exposes erasure, ships it. The class default is an Azure AI Search index with
            // no endpoint, which cannot be reached and so cannot be erased.
            ["AppConfig:AI:Rag:VectorStore:Provider"] = "faiss",
            ["AppConfig:AI:Erasure:SubjectReferenceKey"] =
                Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        },
        (services, _) =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
            services.Replace(ServiceDescriptor.Singleton<IMemoryAuditSink>(new CapturingMemoryAuditSink(_memoryEvents)));
        });

    private async Task<(string ConversationId, string NodeId)> SeedPersonAsync(
        ServiceProvider provider, string ownerId, string label)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var conversations = sp.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync("agent", ownerId);
        await conversations.AppendMessageAsync(conversation.Id, ownerId,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, $"{Marker} {label}", DateTimeOffset.UtcNow));

        await sp.GetRequiredService<IConversationBudgetTracker>().RecordUsageAsync(conversation.Id, 100);

        var spill = await sp.GetRequiredService<IToolResultStore>().StoreIfLargeAsync(
            conversation.Id, "search", null, new string('x', 500), scopeIsRetrievable: true);
        spill.IsPersistedToDisk.Should().BeTrue("the seed must actually spill");

        await sp.GetRequiredService<IScheduleStore>().CreateAsync(new ScheduleRecord
        {
            ScheduleId = $"schedule-{label}",
            Kind = RunKind.Workflow,
            TargetId = Guid.NewGuid().ToString(),
            OwnerId = ownerId,
            TenantId = null,
            Envelope = new CapabilityEnvelope(),
            CronExpression = "*/30 * * * *",
            TimeZoneId = "UTC",
            Enabled = true,
            NextFireAt = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 0,
        }, CancellationToken.None);

        // Written system-scoped, as background memory extraction does; the store stamps the canonical owner.
        var nodeId = $"memory:default:{ownerId.ToLowerInvariant()}:{label}-favourite";
        await sp.GetRequiredService<IKnowledgeGraphStore>().AddNodesAsync(
            [new GraphNode { Id = nodeId, Name = $"{label} favourite", Type = "Memory", OwnerId = ownerId }]);

        return (conversation.Id, nodeId);
    }

    private async Task<ErasureReceipt> EraseAsOperatorAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // The operator's own identity is ambient, exactly as KnowledgeScopeMiddleware leaves it for an HTTP request.
        using var operatorScope = sp.GetRequiredService<IKnowledgeScopeWriter>().SetScope(userId: Operator, tenantId: "contoso");
        var result = await sp.GetRequiredService<IMediator>().Send(
            new EraseSubjectDataCommand { SubjectId = Subject, OperatorId = Operator });

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Value!;
    }

    private string SpillDirectory(string conversationId) => Path.Combine(_workingDir, "spills", conversationId);

    private IEnumerable<string> SpilledFiles(string conversationId) =>
        Directory.Exists(SpillDirectory(conversationId))
            ? Directory.EnumerateFiles(SpillDirectory(conversationId), "*", SearchOption.AllDirectories)
            : [];

    private IEnumerable<string> EverythingRecorded() =>
        _logs.Messages
            .Concat(_memoryEvents
                .Where(e => e.Action is MemoryAuditAction.Forget or MemoryAuditAction.Erasure)
                .SelectMany(e => new[] { e.ActorId, e.ScopeId }
                    .Concat(e.AffectedNodeIds ?? [])
                    .Concat(e.AffectedEdgeIds ?? [])));

    private sealed class CapturingMemoryAuditSink(ConcurrentQueue<MemoryAuditEvent> events) : IMemoryAuditSink
    {
        public Task EmitAsync(MemoryAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            events.Enqueue(auditEvent);
            return Task.CompletedTask;
        }

        public Task EmitBatchAsync(IReadOnlyList<MemoryAuditEvent> auditEvents, CancellationToken cancellationToken = default)
        {
            foreach (var auditEvent in auditEvents)
                events.Enqueue(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
        }
    }
}
