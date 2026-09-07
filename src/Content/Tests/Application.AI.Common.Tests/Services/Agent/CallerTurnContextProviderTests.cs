using Application.AI.Common.Services;
using Application.AI.Common.Services.Agent;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="CallerTurnContextProvider"/> — the rail that delivers
/// <see cref="CallerTurnContextScope.Current"/> to the model fresh every turn, without touching the
/// static instructions the agent was built with.
/// </summary>
/// <remarks>
/// Drives the public <see cref="AIContextProvider.InvokingAsync"/> rather than the protected hook,
/// matching <c>PerTurnBudgetContextProviderTests</c> — the base merge
/// (<c>Instructions = input + "\n" + provided</c>) is part of the behaviour under test, since a
/// provider that echoed the input back would duplicate the entire static system prompt every turn.
/// </remarks>
public sealed class CallerTurnContextProviderTests : IDisposable
{
    private static AIContextProvider.InvokingContext MakeContext(string? staticInstructions) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, new AIContext
        {
            Instructions = staticInstructions,
            Messages = new List<ChatMessage> { new(ChatRole.User, "hi") },
        });

    public void Dispose() => CallerTurnContextScope.Current = null;

    [Fact]
    public async Task NoAmbientValue_ContributesNothing()
    {
        CallerTurnContextScope.Current = null;

        var result = await new CallerTurnContextProvider().InvokingAsync(MakeContext("Static."));

        result.Instructions.Should().Be("Static.",
            "an agent that never uses this feature must see byte-identical instructions to before");
    }

    [Fact]
    public async Task AmbientValueSet_AppendedAfterStaticInstructions()
    {
        CallerTurnContextScope.Current = "Mood: relaxed. Recently discussed: the Mac Mini migration.";

        var result = await new CallerTurnContextProvider().InvokingAsync(MakeContext("Static."));

        result.Instructions.Should().Be("Static.\nMood: relaxed. Recently discussed: the Mac Mini migration.");
    }

    [Fact]
    public async Task AmbientValueChangesBetweenCalls_EachCallReflectsItsOwnValue()
    {
        var provider = new CallerTurnContextProvider();

        CallerTurnContextScope.Current = "Turn one context.";
        var first = await provider.InvokingAsync(MakeContext("Static."));

        CallerTurnContextScope.Current = "Turn two context — completely different.";
        var second = await provider.InvokingAsync(MakeContext("Static."));

        // The whole point of this rail: the static instructions stay identical while this changes
        // every turn, which is what keeps the frozen prefix cache-eligible.
        first.Instructions.Should().Be("Static.\nTurn one context.");
        second.Instructions.Should().Be("Static.\nTurn two context — completely different.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankAmbientValue_ContributesNothing(string? value)
    {
        CallerTurnContextScope.Current = value;

        var result = await new CallerTurnContextProvider().InvokingAsync(MakeContext("Static."));

        result.Instructions.Should().Be("Static.");
    }
}
