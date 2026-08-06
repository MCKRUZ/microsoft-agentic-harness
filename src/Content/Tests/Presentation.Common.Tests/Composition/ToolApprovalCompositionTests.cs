using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves human approval routing for mid-turn tool calls is live on the REAL composition root: a
/// tool call the governor will not auto-approve reaches the escalation subsystem, and the human's
/// answer decides whether it executes.
/// </summary>
/// <remarks>
/// <para>
/// The governor could always conclude "this requires approval" — and then blocked, because nothing
/// carried the question to a human. The machinery to ask was built and wired
/// (<c>EscalationRequest</c> even carries <c>ToolName</c> and <c>Arguments</c>) but had no caller on
/// this path. These tests pin the connection end to end.
/// </para>
/// <para>
/// <strong>Why composition-root and not unit tests.</strong> The router's own unit tests pass with
/// its DI registration deleted — the lesson from the four unit tests that stayed green while a
/// missing registration made a control inert. Everything here resolves from the graph real hosts
/// build, so removing the registration, changing a lifetime, or failing to consult the router fails
/// these tests. The only substitution is <see cref="IEscalationService"/> itself, which is an
/// external boundary: resolving it for real would wait on an actual human.
/// </para>
/// </remarks>
public sealed class ToolApprovalCompositionTests : IDisposable
{
    private const string HostSkillId = "approval-host-skill";
    private const string ToolName = "wire_funds";
    private const string Approver = "alice";

    private readonly string _tempRoot;
    private readonly string _skillsDir;

    public ToolApprovalCompositionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "composition-approval-" + Guid.NewGuid().ToString("N"));
        _skillsDir = Path.Combine(_tempRoot, "skills");

        var skillDir = Path.Combine(_skillsDir, "host");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {HostSkillId}
            description: A built-in skill used to resolve a governed tool.
            ---
            Host instructions.
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    /// <summary>
    /// Enforcement on, the permission default set to Ask so every tool resolves to "requires
    /// approval", and approval routing armed with a one-name roster.
    /// </summary>
    private Dictionary<string, string?> Settings(bool approvalEnabled, bool escalationEnabled = true) => new()
    {
        ["AppConfig:AI:Skills:BasePath"] = _skillsDir,
        ["AppConfig:AI:Governance:EnforceToolInvocation"] = "true",
        ["AppConfig:AI:Permissions:DefaultBehavior"] = "Ask",
        ["AppConfig:AI:Governance:Escalation:Enabled"] = escalationEnabled ? "true" : "false",
        ["AppConfig:AI:Governance:ToolApproval:Enabled"] = approvalEnabled ? "true" : "false",
        ["AppConfig:AI:Governance:ToolApproval:Approvers:0"] = Approver,
        ["AppConfig:AI:Governance:ToolApproval:TimeoutSeconds"] = "30",
    };

    [Fact]
    public void ApprovalRouter_IsRegisteredOnTheProductionGraph()
    {
        using var provider = CompositionRootTestHost.BuildProvider(Settings(approvalEnabled: true));
        using var scope = provider.CreateScope();

        var router = scope.ServiceProvider.GetRequiredService<IToolApprovalRouter>();

        router.Should().BeOfType<EscalationToolApprovalRouter>(
            "the governor's approval verdict must reach the escalation-backed router, not a stub");
    }

    [Fact]
    public async Task ApprovalRequiredTool_HumanApproves_ExecutesOnTheLivePath()
    {
        // The behaviour the whole feature exists for: before this, an Ask verdict was a dead end and
        // this tool could never run no matter who said yes.
        var escalation = new StubEscalationService(approve: true);
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings(approvalEnabled: true), (services, _) => services.AddSingleton<IEscalationService>(escalation));

        var executed = false;
        var tool = await BuildGovernedTool(provider,
            AIFunctionFactory.Create(() => { executed = true; return "transferred"; }, ToolName));

        using var scope = provider.CreateScope();
        var (result, trace) = await InvokeUnderGovernedTurn(scope, tool);

        executed.Should().BeTrue("a human approved the call, so it must actually run");
        ResultText(result).Should().Be("transferred");
        escalation.Requests.Should().ContainSingle()
            .Which.ToolName.Should().Be(ToolName, "the approver must be told which tool they are approving");
        trace.ToolDecisions.Should().ContainSingle()
            .Which.Should().Match<ToolDecisionRecord>(d =>
                d.Outcome == ToolDecisionOutcome.Allowed && d.RequiredApproval && d.ApprovalGranted);
    }

    [Fact]
    public async Task ApprovalRequiredTool_HumanRefuses_StillBlockedOnTheLivePath()
    {
        var escalation = new StubEscalationService(approve: false);
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings(approvalEnabled: true), (services, _) => services.AddSingleton<IEscalationService>(escalation));

        var executed = false;
        var tool = await BuildGovernedTool(provider,
            AIFunctionFactory.Create(() => { executed = true; return "transferred"; }, ToolName));

        using var scope = provider.CreateScope();
        var (result, trace) = await InvokeUnderGovernedTurn(scope, tool);

        executed.Should().BeFalse("an approver refused, so the tool must never have run");
        ResultText(result).Should().Contain("is not permitted");
        escalation.Requests.Should().ContainSingle("the human was still asked");
        trace.ToolDecisions.Should().ContainSingle()
            .Which.Outcome.Should().Be(ToolDecisionOutcome.PendingApproval);
    }

    [Fact]
    public async Task ApprovalRoutingOff_NobodyIsAskedAndTheCallBlocks()
    {
        // The regression guard for every deployment that does not opt in: the escalation subsystem
        // must not be touched at all, and the outcome must be the pre-existing block.
        var escalation = new StubEscalationService(approve: true);
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings(approvalEnabled: false), (services, _) => services.AddSingleton<IEscalationService>(escalation));

        var executed = false;
        var tool = await BuildGovernedTool(provider,
            AIFunctionFactory.Create(() => { executed = true; return "transferred"; }, ToolName));

        using var scope = provider.CreateScope();
        var (_, trace) = await InvokeUnderGovernedTurn(scope, tool);

        executed.Should().BeFalse();
        escalation.Requests.Should().BeEmpty(
            "routing is off, so no escalation may be raised — an approving stub must not rescue the call");
        trace.ToolDecisions.Should().ContainSingle()
            .Which.Outcome.Should().Be(ToolDecisionOutcome.PendingApproval);
    }

    [Fact]
    public async Task EscalationSubsystemOff_ApprovalRoutingDoesNotFireEvenWhenEnabled()
    {
        var escalation = new StubEscalationService(approve: true);
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings(approvalEnabled: true, escalationEnabled: false),
            (services, _) => services.AddSingleton<IEscalationService>(escalation));

        var executed = false;
        var tool = await BuildGovernedTool(provider,
            AIFunctionFactory.Create(() => { executed = true; return "transferred"; }, ToolName));

        using var scope = provider.CreateScope();
        await InvokeUnderGovernedTurn(scope, tool);

        executed.Should().BeFalse();
        escalation.Requests.Should().BeEmpty(
            "the escalation subsystem's own master switch must still gate the routing");
    }

    private static async Task<AIFunction> BuildGovernedTool(ServiceProvider provider, AIFunction probe)
    {
        var skill = provider.GetRequiredService<ISkillMetadataRegistry>().TryGet(HostSkillId);
        skill.Should().NotBeNull("the built-in skill must be discoverable from the configured BasePath");

        var tools = await provider.GetRequiredService<IToolChainBuilder>().BuildToolsAsync(
            skill!, new Domain.AI.Skills.SkillAgentOptions { AdditionalTools = [probe] });

        return tools.OfType<AIFunction>().Single(t => t.Name == probe.Name);
    }

    /// <summary>
    /// Invokes a governed function inside a turn shaped exactly like
    /// <c>ExecuteAgentTurnCommandHandler</c>'s — scoped context initialized, the scope's governor
    /// published ambiently, and the accessor cleared in a finally.
    /// </summary>
    private static async Task<(object? Result, GovernanceTrace Trace)> InvokeUnderGovernedTurn(
        IServiceScope scope, AIFunction function)
    {
        scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("composition-approval-agent", "conv-approval", turnNumber: 1);

        var governor = scope.ServiceProvider.GetRequiredService<IToolInvocationGovernor>();
        ToolGovernanceAccessor.Current = governor;
        try
        {
            var result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
            return (result, governor.GetTrace());
        }
        finally
        {
            ToolGovernanceAccessor.Current = null;
        }
    }

    private static string ResultText(object? invocationResult) => invocationResult switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
            => element.GetString()!,
        _ => invocationResult?.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Stands in for the humans. Records every request it is handed so a test can assert whether
    /// anyone was asked at all, and answers immediately with a fixed verdict.
    /// </summary>
    private sealed class StubEscalationService(bool approve) : IEscalationService
    {
        public List<EscalationRequest> Requests { get; } = [];

        public Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new EscalationOutcome
            {
                EscalationId = request.EscalationId,
                IsApproved = approve,
                Decisions =
                [
                    new ApproverDecision
                    {
                        ApproverName = Approver,
                        Approved = approve,
                        RespondedAt = DateTimeOffset.UtcNow
                    }
                ],
                ResolutionType = approve ? EscalationResolutionType.Approved : EscalationResolutionType.Denied,
                ResolvedAt = DateTimeOffset.UtcNow,
                Approvers = request.Approvers
            });
        }

        public Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<EscalationDecisionResult> SubmitDecisionAsync(
            Guid escalationId, ApproverDecision decision, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct) =>
            Task.FromResult<EscalationRequest?>(null);

        public Task<EscalationOutcome?> GetOutcomeAsync(Guid escalationId, CancellationToken ct) =>
            Task.FromResult<EscalationOutcome?>(null);

        public Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EscalationRequest>>([]);

        public Task<EscalationOutcome> CancelEscalationAsync(
            Guid escalationId, string reason, string cancelledBy, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
