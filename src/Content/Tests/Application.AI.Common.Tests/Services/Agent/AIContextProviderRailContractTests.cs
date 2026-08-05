using Application.AI.Common.Tests.Fakes;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Pins the two facts about the framework's context-provider rail that
/// <see cref="Application.AI.Common.Services.Agent.PerTurnBudgetContextProvider"/> is built on, by driving a
/// real <see cref="ChatClientAgent"/> rather than by hand-rolling the chain.
/// </summary>
/// <remarks>
/// <para>
/// Per-turn budget accounting depends entirely on the runtime feeding each provider the accumulated output
/// of the ones before it, seeded with the agent's own instructions and tools. If a future SDK handed every
/// provider the same untouched input instead, the measurer at the end of the rail would see only the
/// baseline, charge nothing, and the budget would silently return to under-reporting — with every test that
/// drives the chain by hand still green. This assembly has shipped that exact shape of defect four times
/// (see <c>AIContextProviderMergeContractTests</c>), which is why the assumption is pinned against the real
/// runtime here and not merely documented.
/// </para>
/// <para>
/// A failure here is a breaking SDK change, not a bug in the harness — but it is the signal that the
/// accounting built on top has stopped working.
/// </para>
/// </remarks>
public sealed class AIContextProviderRailContractTests
{
    private const string SystemPrompt = "STATIC-SYSTEM-PROMPT";
    private const string FirstContribution = "[FIRST-CONTRIBUTION]";

    /// <summary>
    /// Records the context it was handed, then contributes <paramref name="contribution"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AIContextProvider.StateKeys"/> is overridden because the runtime refuses an agent whose
    /// providers do not all carry distinct keys, and the default is the concrete type name — so two
    /// instances of one provider type cannot coexist on a rail.
    /// </remarks>
    private sealed class RecordingProvider(string key, string? contribution) : AIContextProvider
    {
        public AIContext? Saw { get; private set; }

        public override IReadOnlyList<string> StateKeys => [key];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            Saw = context.AIContext;
            return ValueTask.FromResult(new AIContext { Instructions = contribution });
        }
    }

    private static async Task<(RecordingProvider First, RecordingProvider Last)> RunOneTurnAsync()
    {
        var first = new RecordingProvider("first", FirstContribution);
        var last = new RecordingProvider("last", contribution: null);

        var agent = new ChatClientAgent(
            new FakeChatClient().WithDefaultResponse("ok"),
            new ChatClientAgentOptions
            {
                Name = "RailContractAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemPrompt,
                    Tools = [AIFunctionFactory.Create(
                        () => "x", new AIFunctionFactoryOptions { Name = "agent_own_tool", Description = "t" })]
                },
                AIContextProviders = [first, last]
            });

        await agent.RunAsync("hello");

        return (first, last);
    }

    [Fact]
    public async Task TheRail_SeedsTheFirstProviderWithTheAgentsOwnInstructionsAndTools()
    {
        var (first, _) = await RunOneTurnAsync();

        // The measurer subtracts a baseline it is handed at construction. That subtraction is only correct
        // because the rail starts from the agent's own prompt and tools rather than from empty.
        first.Saw!.Instructions.Should().Be(SystemPrompt);
        first.Saw.Tools!.Select(t => t.Name).Should().Equal("agent_own_tool");
    }

    [Fact]
    public async Task TheRail_FeedsEachProviderTheAccumulatedOutputOfTheOnesBeforeIt()
    {
        var (_, last) = await RunOneTurnAsync();

        // The whole reason one measurer at the end of the rail can account for every provider ahead of it.
        // Were this to become "every provider gets the same input", the measurer would see only the
        // baseline and charge zero forever.
        last.Saw!.Instructions.Should().Contain(SystemPrompt).And.Contain(FirstContribution);
        last.Saw.Instructions.Should().NotBe(SystemPrompt);
    }

    [Fact]
    public void TheRail_RefusesProvidersThatShareAStateKey()
    {
        // Why the measurer is one instance at the end rather than a wrapper around each provider: every
        // instance of a single wrapper type would carry that type's name as its state key, and the agent
        // would not construct at all.
        var act = () => new ChatClientAgent(
            new FakeChatClient(),
            new ChatClientAgentOptions
            {
                Name = "CollidingAgent",
                AIContextProviders =
                [
                    new RecordingProvider("same-key", contribution: null),
                    new RecordingProvider("same-key", contribution: null)
                ]
            });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*same state key*");
    }
}
