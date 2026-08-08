using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves the consumer-authored observer seam is live on the REAL composition root: a rule the host
/// registers is consulted on every agent tool call and can stop one — and, critically, cannot
/// resurrect a call the built-in gates already refused.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists because the harness's three built-in observers answer harness questions ("may
/// this agent use this tool", "is the data too sensitive", "is the agent looping") and none answers
/// the consumer's question ("is this specific invocation a good idea"). The chokepoint class is
/// internal and sealed, so before this there was nowhere to put a domain rule.
/// </para>
/// <para>
/// <strong>The load-bearing test here is
/// <see cref="PermissiveObserver_CannotRescueACallTheGovernorDenied"/>.</strong> The entire safety
/// argument for letting consumers inject code into the tool path is that observers run last and can
/// only tighten. If that ordering ever regressed, a well-meaning observer could widen access past
/// the capability envelope — so it is asserted against the production graph rather than trusted.
/// </para>
/// </remarks>
public sealed class ToolCallObserverCompositionTests : IDisposable
{
    private const string ToolName = "wire_funds";

    private readonly GovernedToolTestSkill _skill = new("observer");

    public void Dispose() => _skill.Dispose();

    /// <param name="permission">
    /// The permission default every tool resolves under. "Allow" lets a call reach the observers;
    /// "Deny" has the governor refuse it before they are consulted.
    /// </param>
    private Dictionary<string, string?> Settings(string permission) => new()
    {
        ["AppConfig:AI:Skills:BasePath"] = _skill.SkillsBasePath,
        ["AppConfig:AI:Governance:EnforceToolInvocation"] = "true",
        ["AppConfig:AI:Permissions:DefaultBehavior"] = permission,
    };

    [Fact]
    public void AdmissionChain_IsRegisteredOnTheProductionGraph_WithEveryStageResolvable()
    {
        // Every execution path depends on this one type, so a composition that fails to register it —
        // or registers it without one of its four stages — takes every path down at resolution rather
        // than running any of them unguarded. Resolved from the real root, not a hand-rolled
        // container, because "registered but never wired" is exactly the failure this suite exists for.
        using var provider = CompositionRootTestHost.BuildProvider(Settings("Allow"));
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();

        act.Should().NotThrow().Which.Should().BeOfType<ToolCallAdmissionPipeline>();
    }

    [Fact]
    public void ObserverChain_IsRegisteredAndEmptyOnTheDefaultComposition()
    {
        // Registration is the opt-in: a host that adds no rules must pay nothing on the hot path.
        using var provider = CompositionRootTestHost.BuildProvider(Settings("Allow"));
        using var scope = provider.CreateScope();

        var chain = scope.ServiceProvider.GetRequiredService<IToolCallObserverChain>();

        chain.Should().BeOfType<ToolCallObserverChain>();
        chain.HasObservers.Should().BeFalse(
            "the harness ships no observers of its own — the seam is empty until a consumer fills it");
    }

    [Fact]
    public async Task RegisteredObserver_IsConsultedOnTheLivePath()
    {
        var observer = new RecordingObserver(ToolCallVerdict.Proceed());
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings("Allow"), (services, _) => services.AddSingleton<IToolCallObserver>(observer));

        var executed = false;
        var tool = await _skill.BuildGovernedToolAsync(provider,
            AIFunctionFactory.Create(() => { executed = true; return "sent"; }, ToolName));

        using var scope = provider.CreateScope();
        await _skill.InvokeUnderGovernedTurnAsync(scope, tool);

        observer.Calls.Should().Be(1, "a registered rule must see every agent tool call");
        observer.LastObservation!.ToolName.Should().Be(ToolName);
        executed.Should().BeTrue("the observer raised no objection");
    }

    [Fact]
    public async Task RegisteredObserver_BlocksTheCallOnTheLivePath()
    {
        // The headline capability: a domain rule the harness could never know, stopping a specific
        // invocation before it happens.
        var observer = new RecordingObserver(ToolCallVerdict.Block("amount exceeds the wire limit"));
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings("Allow"), (services, _) => services.AddSingleton<IToolCallObserver>(observer));

        var executed = false;
        var tool = await _skill.BuildGovernedToolAsync(provider,
            AIFunctionFactory.Create(() => { executed = true; return "sent"; }, ToolName));

        using var scope = provider.CreateScope();
        var (result, _) = await _skill.InvokeUnderGovernedTurnAsync(scope, tool);

        executed.Should().BeFalse("the observer blocked the call, so the tool must never have run");
        GovernedToolTestSkill.ResultText(result).Should().Contain("is not permitted");
        GovernedToolTestSkill.ResultText(result).Should().NotContain("wire limit",
            "the rule's reasoning is operator-facing and must not reach the model");
    }

    [Fact]
    public async Task PermissiveObserver_CannotRescueACallTheGovernorDenied()
    {
        // THE load-bearing assertion. Observers run last, after admission control has settled
        // whether the agent may use the tool at all. An observer saying "proceed" must not widen
        // access — and must not even be consulted, since the call was already refused.
        var observer = new RecordingObserver(ToolCallVerdict.Proceed());
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings("Deny"), (services, _) => services.AddSingleton<IToolCallObserver>(observer));

        var executed = false;
        var tool = await _skill.BuildGovernedToolAsync(provider,
            AIFunctionFactory.Create(() => { executed = true; return "sent"; }, ToolName));

        using var scope = provider.CreateScope();
        var (result, trace) = await _skill.InvokeUnderGovernedTurnAsync(scope, tool);

        executed.Should().BeFalse("the governor denied the call; no observer may override that");
        GovernedToolTestSkill.ResultText(result).Should().Contain("is not permitted");
        observer.Calls.Should().Be(0,
            "observers run after admission control — a denied call must never reach consumer code at all");
        trace.ToolDecisions.Should().ContainSingle()
            .Which.Outcome.Should().Be(ToolDecisionOutcome.Denied);
    }

    [Fact]
    public async Task MultipleObservers_FirstObjectionWinsOnTheLivePath()
    {
        var permissive = new RecordingObserver(ToolCallVerdict.Proceed());
        var restrictive = new RecordingObserver(ToolCallVerdict.Block("sanctioned counterparty"));
        await using var provider = CompositionRootTestHost.BuildProvider(
            Settings("Allow"), (services, _) =>
            {
                services.AddSingleton<IToolCallObserver>(permissive);
                services.AddSingleton<IToolCallObserver>(restrictive);
            });

        var executed = false;
        var tool = await _skill.BuildGovernedToolAsync(provider,
            AIFunctionFactory.Create(() => { executed = true; return "sent"; }, ToolName));

        using var scope = provider.CreateScope();
        await _skill.InvokeUnderGovernedTurnAsync(scope, tool);

        executed.Should().BeFalse("a restrictive rule wins regardless of registration order");
        permissive.Calls.Should().Be(1);
        restrictive.Calls.Should().Be(1);
    }

    /// <summary>A consumer rule that answers as the test dictates and records what it was shown.</summary>
    private sealed class RecordingObserver(ToolCallVerdict verdict) : IToolCallObserver
    {
        public string Name => "test-rule";
        public int Calls { get; private set; }
        public ToolCallObservation? LastObservation { get; private set; }

        public ValueTask<ToolCallVerdict> ObserveAsync(
            ToolCallObservation observation, CancellationToken cancellationToken)
        {
            Calls++;
            LastObservation = observation;
            return ValueTask.FromResult(verdict);
        }
    }
}
