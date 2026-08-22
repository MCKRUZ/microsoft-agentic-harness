using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common.Config;
using Microsoft.Extensions.Options;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Application.AI.Common.Tests.CQRS.Changes.Support;

/// <summary>
/// Shared factories for ChangeProposal handler tests — keeps the per-test boilerplate
/// down so the actual scenarios stay readable.
/// </summary>
internal static class TestHelpers
{
    public static readonly AgentIdentity DefaultIdentity = new()
    {
        Id = "agent-001",
        Kind = AgentIdentityKind.ManagedIdentity
    };

    public static readonly DateTimeOffset DefaultTime =
        new(2026, 6, 6, 10, 30, 15, TimeSpan.Zero);

    public static GitRepoTarget DefaultTarget() =>
        new("https://github.com/org/repo", "main", "abc123");

    public static IReadOnlyList<ChangeEdit> DefaultDiff() => new[]
    {
        new ChangeEdit { Op = EditOp.Replace, Target = "foo", Content = "bar" }
    };

    public static ChangeProposal NewProposal(
        ChangeProposalStatus status = ChangeProposalStatus.Draft,
        AgentIdentity? identity = null) =>
        ChangeProposal.Create(
            target: DefaultTarget(),
            diff: DefaultDiff(),
            submittedBy: identity ?? DefaultIdentity,
            summary: "rename foo to bar",
            blastRadius: BlastRadius.Low,
            requiredGates: new[] { "self_validation", "approval", "merge" },
            submittedAt: DefaultTime) with
        {
            Status = status
        };

    public sealed class StubGateResolver : IChangeProposalGateResolver
    {
        public IReadOnlyList<string> ResolvedGates { get; init; } =
            new[] { "self_validation", "approval", "merge" };

        public IReadOnlyList<string> Resolve(ChangeTargetKind targetKind, BlastRadius blastRadius)
            => ResolvedGates;
    }

    public sealed class StubOrchestrator : IChangeProposalOrchestrator
    {
        private readonly InMemoryChangeProposalStore? _store;
        public ChangeProposal? PassThrough { get; set; }
        public int InvocationCount { get; private set; }

        public StubOrchestrator(InMemoryChangeProposalStore? storeForPassThrough = null)
        {
            _store = storeForPassThrough;
        }

        public Task<ChangeProposal?> ProcessAsync(string proposalId, OrchestratorMode mode, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (PassThrough is not null) return Task.FromResult<ChangeProposal?>(PassThrough);
            if (_store is null) return Task.FromResult<ChangeProposal?>(null);
            return _store.GetAsync(proposalId, cancellationToken);
        }
    }

    /// <summary>
    /// In-memory <see cref="IChangeProposalDispatchQueue"/> stub for handler
    /// tests. Records every enqueue so tests can assert "Submit / Approve
    /// handed off to the background worker." Does NOT drive the orchestrator
    /// — the dispatch-to-orchestrator path is covered by
    /// <c>ChangeProposalBackgroundServiceTests</c> in the Infrastructure suite.
    /// </summary>
    public sealed class StubDispatchQueue : IChangeProposalDispatchQueue
    {
        public List<string> Enqueued { get; } = new();

        public ValueTask EnqueueAsync(string proposalId, CancellationToken cancellationToken)
        {
            Enqueued.Add(proposalId);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<string> DequeueAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var id in Enqueued)
            {
                yield return id;
            }
        }
    }

    /// <summary>
    /// Captures every audit append so tests can assert that a human decision reached the durable
    /// chain — and, critically, that it reached it with the reviewer identity the command carried.
    /// </summary>
    public sealed class RecordingAuditWriter : IChangeAuditWriter
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task AppendAsync(
            ChangeProposal proposal,
            GateDecision decision,
            AgentIdentity identity,
            OrchestratorMode mode,
            string correlationId,
            CancellationToken cancellationToken)
        {
            Entries.Add(new AuditEntry(proposal.Id, proposal.Status, decision, mode, correlationId));
            return Task.CompletedTask;
        }

        /// <param name="StatusAtAppend">
        /// The proposal status at append time. Proves audit-before-save ordering: the append must
        /// observe the pre-transition status, never the post-transition one.
        /// </param>
        public sealed record AuditEntry(
            string ProposalId,
            ChangeProposalStatus StatusAtAppend,
            GateDecision Decision,
            OrchestratorMode Mode,
            string CorrelationId);
    }

    /// <summary>
    /// Simulates a durable audit sink that cannot accept the record (disk full, permissions,
    /// chain corruption). The decision must abort rather than persist un-audited.
    /// </summary>
    public sealed class ThrowingAuditWriter : IChangeAuditWriter
    {
        public int Attempts { get; private set; }

        public Task AppendAsync(
            ChangeProposal proposal,
            GateDecision decision,
            AgentIdentity identity,
            OrchestratorMode mode,
            string correlationId,
            CancellationToken cancellationToken)
        {
            Attempts++;
            // Message deliberately resembles the real writer's, which embeds the audit path —
            // the handler must never surface it to the caller.
            throw new IOException(
                @"Failed to append change audit record: C:\secret-audit-path\changes.jsonl is locked.");
        }
    }

    public static IOptionsMonitor<AppConfig> EnabledConfigMonitor(string mode = "Live")
    {
        var cfg = new AppConfig();
        cfg.AI.Changes.Enabled = true;
        cfg.AI.Changes.DefaultMode = mode;
        return new StaticOptionsMonitor<AppConfig>(cfg);
    }

    public static IOptionsMonitor<AppConfig> DisabledConfigMonitor()
    {
        var cfg = new AppConfig();
        cfg.AI.Changes.Enabled = false;
        return new StaticOptionsMonitor<AppConfig>(cfg);
    }

    public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    public sealed class StubAgentContext : IAgentExecutionContext
    {
        public StubAgentContext(AgentIdentity? identity)
        {
            AgentIdentity = identity;
        }

        public string? AgentId { get; private set; }
        public string? ConversationId { get; private set; }
        public int? TurnNumber { get; private set; }
        public string? CallOnceScopeId { get; private set; }
        public AgentIdentity? AgentIdentity { get; private set; }

        public void Initialize(string agentId, string conversationId, int turnNumber, string? callOnceScopeId = null)
        {
            AgentId = agentId;
            ConversationId = conversationId;
            TurnNumber = turnNumber;
            CallOnceScopeId = callOnceScopeId;
        }

        public void SetIdentity(AgentIdentity identity) => AgentIdentity = identity;
    }
}
