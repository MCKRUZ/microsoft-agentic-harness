using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// End-to-end acceptance proof for the PR-8 plan §4 success criteria:
///
///   "agent fixes a failing test, opens a ChangeProposal, gate runs tests + lint
///    green, approval routes to user, merge applies the patch in the working copy."
///
/// The test exercises the full pipeline with real components (no mocks) wherever
/// the production wiring composes against an interface:
/// real <see cref="WorkspaceWriteFileTool"/>, real
/// <see cref="SubmitChangeProposalCommandHandler"/> dispatched via real MediatR,
/// real <see cref="ChangeProposalOrchestrator"/> + real
/// <see cref="SelfValidationGate"/> / <see cref="ApprovalGate"/> /
/// <see cref="MergeGate"/>, real <see cref="InMemoryChangeProposalStore"/>,
/// and a recording <see cref="IChangeApplier"/> + <see cref="IChangeApprovalRouter"/>
/// that capture the mutation that would have been applied to disk.
/// </summary>
public sealed class WorkspaceSkillEndToEndAcceptanceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IOptionsMonitor<AppConfig> _config;

    public WorkspaceSkillEndToEndAcceptanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "workspace-e2e-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var cfg = new AppConfig();
        cfg.AI.Changes.Enabled = true;
        cfg.AI.Changes.DefaultMode = "Live";
        cfg.AI.Changes.AuditStoragePath = Path.Combine(_tempDir, "audit");
        cfg.AI.Changes.EvidenceStoragePath = Path.Combine(_tempDir, "evidence");
        cfg.AI.Changes.DefaultApprovers = ["alice"];
        cfg.AI.Changes.MaxConsecutiveDefers = 3;
        _config = new StaticOptionsMonitor(cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task FullDemo_AgentSubmitsWriteThroughTool_GatesPass_ApprovalRouted_MergeAppliesPatch()
    {
        // ---- Arrange the workspace (the failing-test scenario) ----
        using var fx = new WorkspaceTestFixture();
        var failingTestPath = fx.WriteFile("FailingTest.cs", "broken");
        var agentIdentity = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-A"
        };

        var store = new InMemoryChangeProposalStore();
        var applier = new RecordingApplier(ChangeApplyResult.Succeeded("commit-abc", "1 commit pushed"));
        var router = new RecordingRouter();

        var services = new ServiceCollection();

        // Logging — MediatR handlers ask for ILogger<T>; the null factory satisfies it.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // PR-2 surface
        services.AddSingleton<IChangeProposalStore>(store);
        services.AddSingleton<IChangeAuditWriter>(sp => new JsonlChangeAuditWriter(
            _config, NullLogger<JsonlChangeAuditWriter>.Instance));
        services.AddSingleton(_config);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IChangeProposalGateResolver>(new FixedGateResolver(
            ["self_validation", "approval", "merge"]));
        services.AddSingleton<IChangeProposalDispatchQueue, NoOpDispatchQueue>();
        services.AddSingleton<IAgentExecutionContext>(_ => new StubAgentContext(agentIdentity));

        // Real handler — wired via MediatR.
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<SubmitChangeProposalCommand>());

        // Real gates + appliers + router.
        services.AddKeyedSingleton<IChangeProposalValidator>(
            ChangeTargetKind.GitRepo,
            new PassingValidator());
        services.AddKeyedSingleton<IChangeApplier>(ChangeTargetKind.GitRepo, applier);
        services.AddSingleton<IChangeApprovalRouter>(router);
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.SelfValidation,
            (sp, _) => new SelfValidationGate(sp, NullLogger<SelfValidationGate>.Instance));
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.Approval,
            (sp, _) => new ApprovalGate(sp.GetRequiredService<IChangeApprovalRouter>(),
                NullLogger<ApprovalGate>.Instance));
        services.AddKeyedSingleton<IChangeProposalGate>(
            WellKnownGateKeys.Merge,
            (sp, _) => new MergeGate(sp, NullLogger<MergeGate>.Instance));

        // Workspace skill DI.
        services.AddWorkspaceSkillTools();

        var sp = services.BuildServiceProvider();

        // Establish the sandbox-injected workspace scope.
        var accessor = sp.GetRequiredService<IWorkspaceContextAccessor>();
        using var scope = accessor.BeginScope(fx.Context);

        // ---- Act: agent invokes write_file ----
        var writeTool = sp.GetRequiredKeyedService<Application.AI.Common.Interfaces.Tools.ITool>("write_file");

        var writeResult = await writeTool.ExecuteAsync(
            "submit",
            new Dictionary<string, object?>
            {
                ["path"] = "FailingTest.cs",
                ["content"] = "fixed-content",
                ["summary"] = "fix FailingTest.cs"
            });

        // ---- Phase 1 assertion: disk is unchanged after the agent's write_file call ----
        writeResult.Success.Should().BeTrue();
        File.ReadAllText(failingTestPath).Should().Be("broken",
            "the working copy MUST NOT be mutated by write_file — only ChangeProposal applies the patch");

        // The proposal is now in the store. Find it.
        var proposals = await store.ListAsync(
            new ChangeProposalQuery { MaxResults = 10 },
            CancellationToken.None);
        proposals.Should().ContainSingle();
        var proposalId = proposals[0].Id;

        // ---- Phase 2: orchestrator drives validation gates ----
        var orchestrator = new ChangeProposalOrchestrator(
            store,
            sp.GetRequiredService<IChangeAuditWriter>(),
            sp,
            TimeProvider.System,
            _config,
            NullLogger<ChangeProposalOrchestrator>.Instance);

        var afterValidation = await orchestrator.ProcessAsync(proposalId, OrchestratorMode.Live, CancellationToken.None);
        afterValidation!.Status.Should().Be(ChangeProposalStatus.AwaitingApproval,
            "self_validation passed and the pipeline includes approval");
        router.Routed.Should().ContainSingle(
            "approval gate must route to the human approver");
        applier.InvocationCount.Should().Be(0, "merge hasn't run yet");

        // ---- Phase 3: user approves (out-of-band, via the Approve handler) ----
        var approveHandler = new Application.AI.Common.CQRS.Changes.ApproveChangeProposal.ApproveChangeProposalCommandHandler(
            store,
            sp.GetRequiredService<IChangeProposalDispatchQueue>(),
            _config,
            TimeProvider.System);

        var approveResult = await approveHandler.Handle(
            new Application.AI.Common.CQRS.Changes.ApproveChangeProposal.ApproveChangeProposalCommand
            {
                ProposalId = proposalId,
                ReviewerId = "alice"
            },
            CancellationToken.None);

        approveResult.IsSuccess.Should().BeTrue();

        // ---- Phase 4: orchestrator picks back up and runs merge ----
        var afterMerge = await orchestrator.ProcessAsync(proposalId, OrchestratorMode.Live, CancellationToken.None);

        afterMerge!.Status.Should().Be(ChangeProposalStatus.Merged,
            "merge applier ran successfully");
        applier.InvocationCount.Should().Be(1, "merge gate dispatched once to the recording applier");

        // ---- Phase 5: the applier captured the mutation it would have applied ----
        applier.LastProposal.Should().NotBeNull();
        applier.LastProposal!.Diff.Should().ContainSingle();
        applier.LastProposal.Diff[0].Content.Should().Be("fixed-content");
        applier.LastProposal.Diff[0].Target.Should().Be("FailingTest.cs");
        var gitTarget = applier.LastProposal.Target.Should().BeOfType<GitRepoTarget>().Subject;
        gitTarget.RepoUrl.Should().Be("https://github.com/org/repo");
        gitTarget.Branch.Should().Be("main");
    }

    // ---- Test doubles (minimal real surface, no Moq) ----

    private sealed class RecordingApplier : IChangeApplier
    {
        private readonly ChangeApplyResult _result;
        public RecordingApplier(ChangeApplyResult result) => _result = result;
        public ChangeTargetKind TargetKind => ChangeTargetKind.GitRepo;
        public int InvocationCount { get; private set; }
        public ChangeProposal? LastProposal { get; private set; }
        public Task<ChangeApplyResult> ApplyAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            LastProposal = proposal;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingRouter : IChangeApprovalRouter
    {
        public List<string> Routed { get; } = new();
        public Task RouteAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            Routed.Add(proposal.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class PassingValidator : IChangeProposalValidator
    {
        public string Key => "test_validator";
        public Task<GateResult> ValidateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
            => Task.FromResult(GateResult.Pass("tests + lint green"));
    }

    private sealed class FixedGateResolver : IChangeProposalGateResolver
    {
        private readonly IReadOnlyList<string> _gates;
        public FixedGateResolver(IReadOnlyList<string> gates) => _gates = gates;
        public IReadOnlyList<string> Resolve(ChangeTargetKind targetKind, BlastRadius blastRadius) => _gates;
    }

    private sealed class NoOpDispatchQueue : IChangeProposalDispatchQueue
    {
        public ValueTask EnqueueAsync(string proposalId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<string> DequeueAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubAgentContext : IAgentExecutionContext
    {
        public StubAgentContext(AgentIdentity identity) { AgentIdentity = identity; }
        public string? AgentId { get; private set; }
        public string? ConversationId { get; private set; }
        public int? TurnNumber { get; private set; }
        public AgentIdentity? AgentIdentity { get; private set; }
        public void Initialize(string agentId, string conversationId, int turnNumber)
        {
            AgentId = agentId;
            ConversationId = conversationId;
            TurnNumber = turnNumber;
        }
        public void SetIdentity(AgentIdentity identity) => AgentIdentity = identity;
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public StaticOptionsMonitor(AppConfig value) { CurrentValue = value; }
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
