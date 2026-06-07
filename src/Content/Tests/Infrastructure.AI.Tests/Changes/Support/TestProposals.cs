using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Identity;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Infrastructure.AI.Tests.Changes.Support;

internal static class TestProposals
{
    public static readonly AgentIdentity DefaultIdentity = new()
    {
        Id = "agent-001",
        Kind = AgentIdentityKind.ManagedIdentity,
        TenantId = "tenant-A"
    };

    public static readonly DateTimeOffset DefaultTime =
        new(2026, 6, 6, 10, 30, 15, TimeSpan.Zero);

    public static ChangeProposal NewProposal(
        IReadOnlyList<string>? gates = null,
        BlastRadius blastRadius = BlastRadius.Low) =>
        ChangeProposal.Create(
            target: new GitRepoTarget("https://github.com/org/repo", "main", "abc123"),
            diff: new[] { new ChangeEdit { Op = EditOp.Replace, Target = "foo", Content = "bar" } },
            submittedBy: DefaultIdentity,
            summary: "rename foo to bar",
            blastRadius: blastRadius,
            requiredGates: gates ?? new[]
            {
                WellKnownGateKeys.SelfValidation,
                WellKnownGateKeys.Approval,
                WellKnownGateKeys.Merge
            },
            submittedAt: DefaultTime);

    /// <summary>
    /// Infer the gate phase from a well-known key so existing tests that pass
    /// <c>WellKnownGateKeys.Approval</c> or <c>WellKnownGateKeys.Merge</c> don't
    /// have to pass the phase explicitly. Unknown keys default to
    /// <see cref="GatePhase.Validation"/>, matching the orchestrator's
    /// no-gate-registered fallback.
    /// </summary>
    internal static GatePhase InferPhase(string key) => key switch
    {
        WellKnownGateKeys.Approval => GatePhase.Approval,
        WellKnownGateKeys.Merge => GatePhase.Merge,
        _ => GatePhase.Validation,
    };

    public sealed class StubGate : IChangeProposalGate
    {
        private readonly Queue<GateResult> _scripted;
        public StubGate(string key, GateResult single, GatePhase? phase = null)
            : this(key, new[] { single }, phase) { }
        public StubGate(string key, IEnumerable<GateResult> scripted, GatePhase? phase = null)
        {
            Key = key;
            Phase = phase ?? InferPhase(key);
            _scripted = new Queue<GateResult>(scripted);
        }

        public string Key { get; }
        public GatePhase Phase { get; }
        public int InvocationCount { get; private set; }

        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            var next = _scripted.Count > 1 ? _scripted.Dequeue() : _scripted.Peek();
            return Task.FromResult(next);
        }
    }

    public sealed class ThrowingGate(string key, GatePhase? phase = null) : IChangeProposalGate
    {
        public string Key { get; } = key;
        public GatePhase Phase { get; } = phase ?? InferPhase(key);
        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("gate exploded");
    }
}
