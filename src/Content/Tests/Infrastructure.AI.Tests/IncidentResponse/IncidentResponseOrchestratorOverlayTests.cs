using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.IncidentResponse;
using Domain.AI.Changes;
using Domain.Common.Config.AI.IncidentResponse;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.IncidentResponse;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.IncidentResponse;

/// <summary>
/// Integration tests that wire <see cref="ChangeProposalOrchestrator"/> with
/// the PR-5 incident overlay. Verifies that (a) when an incident is active
/// with additional gates, the orchestrator runs those gates during the
/// appropriate phase, (b) the proposal's stored <c>RequiredGates</c> is never
/// mutated, and (c) the audit history records the overlay so a reviewer can
/// see why extra gates ran.
/// </summary>
public sealed class IncidentResponseOrchestratorOverlayTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryChangeProposalStore _store;
    private readonly JsonlChangeAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig> _monitor;

    public IncidentResponseOrchestratorOverlayTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _store = new InMemoryChangeProposalStore();
        _audit = new JsonlChangeAuditWriter(monitor, TimeProvider.System, NullLogger<JsonlChangeAuditWriter>.Instance);
        _monitor = monitor;
    }

    public void Dispose()
    {
        _audit.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class StubIncidentContext : IIncidentContext
    {
        public StubIncidentContext(string? incidentType) { CurrentIncidentType = incidentType; }
        public string? CurrentIncidentType { get; private set; }
        public void Set(string? incidentType) { CurrentIncidentType = incidentType; }
    }

    private sealed class StubIncidentResolver : IIncidentResponsePlanResolver
    {
        private readonly IncidentResponsePlan? _plan;
        public StubIncidentResolver(IncidentResponsePlan? plan) { _plan = plan; }
        public IncidentResponsePlan? ResolveFor(string? incidentType) => _plan;
    }

    private ChangeProposalOrchestrator BuildSut(
        IIncidentContext? incidentContext,
        IIncidentResponsePlanResolver? incidentResolver,
        params IChangeProposalGate[] gates)
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
            NullLogger<ChangeProposalOrchestrator>.Instance,
            incidentContext,
            incidentResolver);
    }

    [Fact]
    public async Task ProcessAsync_NoIncident_BehavesIdenticallyToPR2Path()
    {
        // No incident context wired — orchestrator runs only the proposal's stored gates.
        var proposal = TestProposals.NewProposal(gates:
        [
            WellKnownGateKeys.SelfValidation,
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge
        ]);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var complianceGate = new TestProposals.StubGate("compliance", GateResult.Pass());

        var sut = BuildSut(
            incidentContext: null,
            incidentResolver: null,
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("auto")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()),
            complianceGate);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        complianceGate.InvocationCount.Should().Be(0,
            because: "no incident is active so the incident-added compliance gate must not run");
    }

    [Fact]
    public async Task ProcessAsync_IncidentActive_OverlaysAdditionalGatesAtRuntime()
    {
        var proposal = TestProposals.NewProposal(gates:
        [
            WellKnownGateKeys.SelfValidation,
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge
        ]);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var plan = new IncidentResponsePlan
        {
            Name = "DataExfil",
            IncidentType = "DataExfiltrationSuspected",
            AdditionalRequiredGates = ["compliance"]
        };

        var complianceGate = new TestProposals.StubGate("compliance", GateResult.Pass("policy ok"));

        var sut = BuildSut(
            incidentContext: new StubIncidentContext("DataExfiltrationSuspected"),
            incidentResolver: new StubIncidentResolver(plan),
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("auto")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()),
            complianceGate);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        complianceGate.InvocationCount.Should().Be(1,
            because: "the active incident plan added the compliance gate to the validation phase");

        // Stored RequiredGates MUST be untouched — the orchestrator overlays at
        // evaluation time only. The audit trail records the overlay separately.
        result.RequiredGates.Should().Equal(
            WellKnownGateKeys.SelfValidation,
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge);
    }

    [Fact]
    public async Task ProcessAsync_IncidentActive_AuditTrailRecordsOverlay()
    {
        var proposal = TestProposals.NewProposal(gates:
        [
            WellKnownGateKeys.SelfValidation,
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge
        ]);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var plan = new IncidentResponsePlan
        {
            Name = "DataExfil",
            IncidentType = "DataExfiltrationSuspected",
            AdditionalRequiredGates = ["compliance"]
        };

        var sut = BuildSut(
            incidentContext: new StubIncidentContext("DataExfiltrationSuspected"),
            incidentResolver: new StubIncidentResolver(plan),
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("auto")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()),
            new TestProposals.StubGate("compliance", GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.History.Should().Contain(d =>
            d.GateKey == "incident_overlay"
            && d.Action == GateAction.Pass
            && d.Reason.Contains("DataExfil", StringComparison.Ordinal)
            && d.Reason.Contains("compliance", StringComparison.Ordinal),
            because: "reviewers must be able to see which plan augmented the gate set and which gates it added");
    }

    [Fact]
    public async Task ProcessAsync_IncidentGateAlreadyInProposal_DeduplicatesAndDoesNotDoubleRun()
    {
        // Proposal already lists "compliance" — the plan's same key must not
        // run the gate twice or shift its position.
        var proposal = TestProposals.NewProposal(gates:
        [
            WellKnownGateKeys.SelfValidation,
            "compliance",
            WellKnownGateKeys.Approval,
            WellKnownGateKeys.Merge
        ]);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var plan = new IncidentResponsePlan
        {
            Name = "Dup",
            IncidentType = "AnyIncident",
            AdditionalRequiredGates = ["compliance"]
        };

        var complianceGate = new TestProposals.StubGate("compliance", GateResult.Pass());

        var sut = BuildSut(
            incidentContext: new StubIncidentContext("AnyIncident"),
            incidentResolver: new StubIncidentResolver(plan),
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("auto")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()),
            complianceGate);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        complianceGate.InvocationCount.Should().Be(1,
            because: "the orchestrator de-duplicates incident-added gates already present in the proposal");
    }

    [Fact]
    public async Task ProcessAsync_PlanWithNoAdditionalGates_SkipsOverlayMarker()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var plan = new IncidentResponsePlan
        {
            Name = "NoExtraGates",
            IncidentType = "AnyIncident"
            // AdditionalRequiredGates default = []
        };

        var sut = BuildSut(
            incidentContext: new StubIncidentContext("AnyIncident"),
            incidentResolver: new StubIncidentResolver(plan),
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("auto")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.History.Should().NotContain(d => d.GateKey == "incident_overlay",
            because: "no overlay was applied; the overlay marker would be noise");
    }
}
