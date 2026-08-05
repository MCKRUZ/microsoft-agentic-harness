using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services.Agent;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="PerTurnBudgetContextProvider"/>, the measurer that charges what the context-provider
/// rail injects into every turn (issue #266).
/// </summary>
/// <remarks>
/// Every test drives the public <see cref="AIContextProvider.InvokingAsync"/> rather than the protected
/// hook, because that is what the runtime calls and because the base merge is part of the behaviour under
/// test — a provider that measured correctly but disturbed the context it measured would pass a hook-level
/// test and corrupt every turn.
/// </remarks>
public sealed class PerTurnBudgetContextProviderTests
{
    private const string Agent = "BudgetAgent";
    private const string Baseline = "STATIC-SYSTEM-PROMPT";

    /// <summary>Records what the provider charged, in the order it charged it.</summary>
    private sealed class RecordingTracker : Mock<IContextBudgetTracker>
    {
        public List<(string Agent, string Component, int Tokens)> Charges { get; } = [];

        public RecordingTracker() =>
            Setup(t => t.RecordAllocation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                .Callback<string, string, int>((a, c, t) => Charges.Add((a, c, t)));
    }

    private static AITool MakeTool(string name) => AIFunctionFactory.Create(
        () => "ok", new AIFunctionFactoryOptions { Name = name, Description = "t" });

    private static AIContextProvider.InvokingContext MakeContext(AIContext aiContext) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, aiContext);

    private static PerTurnBudgetContextProvider Create(
        IContextBudgetTracker tracker,
        string? baseline = Baseline,
        int baselineToolCount = 0) =>
        new(Agent, tracker, baseline, baselineToolCount,
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
        var tracker = new RecordingTracker();

        await Create(tracker.Object).InvokingAsync(MakeContext(Accumulated(injected)));

        // The exact token figure, not merely "something was charged": billing the whole accumulated
        // string would double-charge the system prompt on every single turn, which is a larger error
        // than the one this class exists to fix and would look like a working feature.
        tracker.Charges.Should().ContainSingle()
            .Which.Tokens.Should().Be(TokenEstimationHelper.EstimateTokens(injected));
    }

    [Fact]
    public async Task Charges_EveryTurn_BecauseThisCostRecurs()
    {
        const string injected = "\nthe skills index card, re-sent with every request";
        var tracker = new RecordingTracker();
        var provider = Create(tracker.Object);

        await provider.InvokingAsync(MakeContext(Accumulated(injected)));
        await provider.InvokingAsync(MakeContext(Accumulated(injected)));
        await provider.InvokingAsync(MakeContext(Accumulated(injected)));

        // This is the whole point of #266. A once-only charge would leave the budget drifting further
        // from reality the longer a conversation runs, which is the defect, not the fix.
        var expected = TokenEstimationHelper.EstimateTokens(injected);
        tracker.Charges.Should().HaveCount(3);
        tracker.Charges.Should().OnlyContain(c => c.Tokens == expected);
    }

    [Fact]
    public async Task Charges_ToolsTheRailContributed_AboveTheAgentsOwnTools()
    {
        var tracker = new RecordingTracker();

        // Two tools were charged at build time; the framework's three disclosure tools arrive here.
        await Create(tracker.Object, baselineToolCount: 2)
            .InvokingAsync(MakeContext(Accumulated(
                injected: string.Empty,
                "file_system", "calculator",
                "load_skill", "read_skill_resource", "run_skill_script")));

        tracker.Charges.Should().ContainSingle()
            .Which.Tokens.Should().Be(TokenEstimationHelper.EstimateToolSchemaTokens(3));
    }

    [Fact]
    public async Task Charges_Nothing_WhenTheRailAddedNothing()
    {
        var tracker = new RecordingTracker();

        await Create(tracker.Object, baselineToolCount: 2)
            .InvokingAsync(MakeContext(Accumulated(injected: string.Empty, "alpha", "beta")));

        tracker.Charges.Should().BeEmpty(
            "an agent whose providers contributed nothing this turn spent nothing this turn");
    }

    [Fact]
    public async Task Charges_Nothing_AndDoesNotThrow_WhenTheContextIsSmallerThanTheBaseline()
    {
        var tracker = new RecordingTracker();

        var shrunk = new AIContext
        {
            Instructions = "tiny",
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = []
        };

        var act = async () => await Create(tracker.Object, baselineToolCount: 5)
            .InvokingAsync(MakeContext(shrunk));

        await act.Should().NotThrowAsync();
        tracker.Charges.Should().BeEmpty("a negative difference is not a negative cost");
    }

    [Fact]
    public async Task Charges_UnderTheSharedComponentName()
    {
        var tracker = new RecordingTracker();

        await Create(tracker.Object).InvokingAsync(MakeContext(Accumulated("\nsomething")));

        // A component name spelled differently here than the dashboard reads does not fail anything —
        // it silently splits one slice of the budget into two.
        tracker.Charges.Should().ContainSingle()
            .Which.Should().Match<(string Agent, string Component, int Tokens)>(c =>
                c.Agent == Agent && c.Component == ContextConventions.BudgetComponents.PerTurnContext);
    }

    [Fact]
    public async Task ContributesNothing_LeavingTheContextExactlyAsItFoundIt()
    {
        var tracker = new RecordingTracker();
        var incoming = Accumulated("\ninjected block", "alpha", "beta");

        var result = await Create(tracker.Object).InvokingAsync(MakeContext(incoming));

        // Observing must not perturb. The base merge appends whatever a provider returns, so anything
        // other than an empty contribution would add text or duplicate tools on every turn.
        result.Instructions.Should().Be(incoming.Instructions);
        result.Tools?.Select(t => t.Name).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task Charges_TheWholeContext_WhenTheAgentHasNoStaticPrompt()
    {
        var tracker = new RecordingTracker();
        var noBaseline = new AIContext
        {
            Instructions = "everything here came from the rail",
            Messages = new List<ChatMessage> { new(ChatRole.User, "go") },
            Tools = []
        };

        await Create(tracker.Object, baseline: null).InvokingAsync(MakeContext(noBaseline));

        tracker.Charges.Should().ContainSingle()
            .Which.Tokens.Should().Be(TokenEstimationHelper.EstimateTokens(noBaseline.Instructions));
    }

    [Fact]
    public void Constructor_RefusesABlankAgentName()
    {
        // A blank name would file these tokens under a budget nobody reads, and the under-reporting
        // this class exists to fix would persist while looking fixed.
        var act = () => new PerTurnBudgetContextProvider(
            "  ", new RecordingTracker().Object, Baseline, 0,
            NullLogger<PerTurnBudgetContextProvider>.Instance);

        act.Should().Throw<ArgumentException>();
    }
}
