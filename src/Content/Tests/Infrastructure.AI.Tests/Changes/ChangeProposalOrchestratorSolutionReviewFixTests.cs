using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Regression coverage for the 2026-06-11 solution review finding that raw gate
/// exception messages were persisted into the <see cref="GateDecision.Reason"/>
/// field — which flows into the proposal's <c>History</c> (returned to callers)
/// and into the <c>changes.jsonl</c> audit file. Gate <c>EvaluateAsync</c>
/// implementations call HTTP services whose exceptions routinely embed request
/// URLs with SAS tokens or query-string credentials; those must never reach a
/// persisted sink. The fix records a stable scrubbed code plus the exception
/// type only, with the full exception captured via structured logging.
/// </summary>
public sealed class ChangeProposalOrchestratorSolutionReviewFixTests : IDisposable
{
    private const string LeakySecret =
        "sig=abc123SECRET-SAS-TOKEN&se=2026-06-11";

    private readonly string _tempDir;
    private readonly InMemoryChangeProposalStore _store;
    private readonly JsonlChangeAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig> _monitor;

    public ChangeProposalOrchestratorSolutionReviewFixTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _monitor = monitor;
        _store = new InMemoryChangeProposalStore();
        _audit = new JsonlChangeAuditWriter(monitor, NullLogger<JsonlChangeAuditWriter>.Instance);
    }

    public void Dispose()
    {
        _audit.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private ChangeProposalOrchestrator BuildSut(params IChangeProposalGate[] gates)
    {
        var services = new ServiceCollection();
        foreach (var gate in gates)
        {
            services.AddKeyedSingleton(gate.Key, gate);
        }
        return new ChangeProposalOrchestrator(
            _store,
            _audit,
            services.BuildServiceProvider(),
            TimeProvider.System,
            _monitor,
            NullLogger<ChangeProposalOrchestrator>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_GateThrowsWithCredentialInMessage_DoesNotPersistMessageToHistory()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);
        var sut = BuildSut(new SecretLeakingGate(WellKnownGateKeys.SelfValidation));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        var reason = result.History.Last().Reason;
        reason.Should().NotContain(LeakySecret);
        // The scrubbed, stable code and the exception type stay — they're safe and
        // give auditors enough to correlate with the full exception in the logs.
        reason.Should().Be(
            $"{ChangeProposalOrchestrator.GateExceptionReasonCode}: {nameof(InvalidOperationException)}");
    }

    [Fact]
    public async Task ProcessAsync_GateThrowsWithCredentialInMessage_DoesNotWriteMessageToAuditJsonl()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);
        var sut = BuildSut(new SecretLeakingGate(WellKnownGateKeys.SelfValidation));

        await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        var auditPath = Path.Combine(_tempDir, "audit", "changes.jsonl");
        var auditContent = await File.ReadAllTextAsync(auditPath);
        auditContent.Should().NotContain(LeakySecret);
        auditContent.Should().Contain(ChangeProposalOrchestrator.GateExceptionReasonCode);
    }

    /// <summary>
    /// Stand-in for an HTTP-backed gate whose exception text embeds a credential —
    /// the exact leak class this regression test guards against.
    /// </summary>
    private sealed class SecretLeakingGate(string key) : IChangeProposalGate
    {
        public string Key { get; } = key;
        public GatePhase Phase { get; } = GatePhase.Validation;

        public Task<GateResult> EvaluateAsync(
            ChangeProposal proposal,
            GateContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"GET https://store.blob.core.windows.net/x?{LeakySecret} failed (403)");
    }
}
