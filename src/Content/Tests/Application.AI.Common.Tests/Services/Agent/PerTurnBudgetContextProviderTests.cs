using Application.AI.Common.Helpers;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="PerTurnBudgetContextProvider"/>, the measurer that charges what the context-provider
/// rail injects into every turn (issue #266).
/// </summary>
/// <remarks>
/// <para>
/// Every test drives the public <see cref="AIContextProvider.InvokingAsync"/> rather than the protected
/// hook, because that is what the runtime calls and because the base merge is part of the behaviour under
/// test — a provider that measured correctly but disturbed the context it measured would pass a hook-level
/// test and corrupt every turn.
/// </para>
/// <para>
/// The budget is the real <see cref="ContextBudgetTracker"/> rather than a mock, so the assertions run
/// through the same <c>RecordAndPublish</c> path production uses and read back the same breakdown a
/// dashboard would. A mocked tracker would prove the provider called something, not that the tokens landed
/// in a component anyone reads.
/// </para>
/// <para>
/// That the rail really does accumulate — the assumption every measurement here depends on — is pinned
/// separately against a real agent in <see cref="AIContextProviderRailContractTests"/>.
/// </para>
/// </remarks>
public sealed class PerTurnBudgetContextProviderTests
{
    private const string Agent = "BudgetAgent";
    private const string Baseline = "STATIC-SYSTEM-PROMPT";

    private static readonly string Component = ContextConventions.BudgetComponents.PerTurnContext;

    private static ContextBudgetTracker NewBudget() => new(
        Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()),
        NullLogger<ContextBudgetTracker>.Instance);

    private static AITool MakeTool(string name) => AIFunctionFactory.Create(
        () => "ok", new AIFunctionFactoryOptions { Name = name, Description = "t" });

    private static AIContextProvider.InvokingContext MakeContext(AIContext aiContext) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, aiContext);

    private static PerTurnBudgetContextProvider Create(
        ContextBudgetTracker budget,
        string? baseline = Baseline,
        int baselineToolCount = 0) =>
        new(Agent, budget, baseline, baselineToolCount,
            NullLogger<PerTurnBudgetContextProvider>.Instance);

    /// <summary>The context as the rail hands it over: baseline plus whatever earlier providers appended.</summary>
    private static AIContext Accumulated(string injected, params string[] toolNames) => new()
    {
        Instructions = Baseline + injected,
        Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
        Tools = [.. toolNames.Select(MakeTool)]
    };

    [Fact]
    public async Task Charges_TheInjectedText_NotTheSystemPromptItWasAppendedTo()
    {
        const string injected = "\nRelevant remembered context: the user prefers dark mode.";
        var budget = NewBudget();

        await Create(budget).InvokingAsync(MakeContext(Accumulated(injected)));

        // The exact token figure, not merely "something was charged": billing the whole accumulated
        // string would double-charge the system prompt on every single turn, which is a larger error
        // than the one this class exists to fix and would look like a working feature.
        budget.GetBreakdown(Agent)[Component]
            .Should().Be(TokenEstimationHelper.EstimateTokens(injected));
    }

    [Fact]
    public async Task Charges_EveryTurn_BecauseThisCostRecurs()
    {
        const string injected = "\nthe skills index card, re-sent with every request";
        var budget = NewBudget();
        var provider = Create(budget);

        await provider.InvokingAsync(MakeContext(Accumulated(injected)));
        await provider.InvokingAsync(MakeContext(Accumulated(injected)));
        await provider.InvokingAsync(MakeContext(Accumulated(injected)));

        // This is the whole point of #266. A once-only charge would leave the budget drifting further
        // from reality the longer a conversation runs, which is the defect, not the fix.
        budget.GetBreakdown(Agent)[Component]
            .Should().Be(TokenEstimationHelper.EstimateTokens(injected) * 3);
    }

    [Fact]
    public async Task Charges_ToolsTheRailContributed_AboveTheAgentsOwnTools()
    {
        var budget = NewBudget();

        // Two tools were charged at build time; the framework's three disclosure tools arrive here.
        await Create(budget, baselineToolCount: 2)
            .InvokingAsync(MakeContext(Accumulated(
                injected: string.Empty,
                "file_system", "calculator",
                "load_skill", "read_skill_resource", "run_skill_script")));

        budget.GetBreakdown(Agent)[Component]
            .Should().Be(TokenEstimationHelper.EstimateToolSchemaTokens(3));
    }

    [Fact]
    public async Task Charges_Nothing_WhenTheRailAddedNothing()
    {
        var budget = NewBudget();

        await Create(budget, baselineToolCount: 2)
            .InvokingAsync(MakeContext(Accumulated(injected: string.Empty, "alpha", "beta")));

        budget.GetBreakdown(Agent).Should().NotContainKey(Component,
            "an agent whose providers contributed nothing this turn spent nothing this turn");
    }

    [Fact]
    public async Task Charges_Nothing_AndDoesNotThrow_WhenTheContextIsSmallerThanTheBaseline()
    {
        var budget = NewBudget();

        var shrunk = new AIContext
        {
            Instructions = "tiny",
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = []
        };

        var act = async () => await Create(budget, baselineToolCount: 5)
            .InvokingAsync(MakeContext(shrunk));

        await act.Should().NotThrowAsync();
        budget.GetBreakdown(Agent).Should().NotContainKey(Component,
            "a negative difference is not a negative cost");
    }

    [Fact]
    public async Task Charges_TheAgentItWasBuiltFor_UnderTheSharedComponentName()
    {
        var budget = NewBudget();

        await Create(budget).InvokingAsync(MakeContext(Accumulated("\nsomething")));

        // A component name spelled differently here than the dashboard reads does not fail anything —
        // it silently splits one slice of the budget into two. Likewise a charge filed under the wrong
        // agent lands in a budget nobody reads.
        budget.GetBreakdown(Agent).Should().ContainKey(Component);
        budget.GetTotalAllocated(Agent).Should().BeGreaterThan(0);
        budget.GetTotalAllocated("SomeOtherAgent").Should().Be(0);
    }

    [Fact]
    public async Task ContributesNothing_LeavingTheContextExactlyAsItFoundIt()
    {
        var incoming = Accumulated("\ninjected block", "alpha", "beta");

        var result = await Create(NewBudget()).InvokingAsync(MakeContext(incoming));

        // Observing must not perturb. The base merge appends whatever a provider returns, so anything
        // other than an empty contribution would add text or duplicate tools on every turn.
        result.Instructions.Should().Be(incoming.Instructions);
        result.Tools?.Select(t => t.Name).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task Charges_TheWholeContext_WhenTheAgentHasNoStaticPrompt()
    {
        var budget = NewBudget();
        var noBaseline = new AIContext
        {
            Instructions = "everything here came from the rail",
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = []
        };

        await Create(budget, baseline: null).InvokingAsync(MakeContext(noBaseline));

        budget.GetBreakdown(Agent)[Component]
            .Should().Be(TokenEstimationHelper.EstimateTokens(noBaseline.Instructions));
    }

    [Fact]
    public void Constructor_RefusesABlankAgentName()
    {
        // A blank name would file these tokens under a budget nobody reads, and the under-reporting
        // this class exists to fix would persist while looking fixed.
        var act = () => new PerTurnBudgetContextProvider(
            "  ", NewBudget(), Baseline, 0,
            NullLogger<PerTurnBudgetContextProvider>.Instance);

        act.Should().Throw<ArgumentException>();
    }
}
