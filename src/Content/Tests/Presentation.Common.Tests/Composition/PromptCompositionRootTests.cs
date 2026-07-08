using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Prompts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Wiring integration tests (audit item I2) for per-request prompt composition (PR #135,
/// audit finding H2) at the FULL composition-root level. The existing
/// <c>PromptComposerScopeIsolationTests</c> bind <c>AddSystemPromptComposition()</c> in
/// isolation; these tests close the remaining wiring gap by proving the composer resolves and
/// composes per-request inside the complete graph that <c>GetServices()</c> builds — where a
/// different registration (or a future one) could reintroduce the captive-singleton bug
/// without the isolated tests noticing.
/// </summary>
/// <remarks>
/// The provider is built with <c>ValidateScopes = true</c>: if the composer (or any section
/// provider it chains) is ever registered as a singleton capturing the scoped
/// <see cref="IAgentExecutionContext"/> again, resolution here throws instead of silently
/// composing against another conversation's state.
/// </remarks>
public sealed class PromptCompositionRootTests
{
    private static ServiceProvider BuildProvider() =>
        CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());

    [Fact]
    public async Task Composer_ResolvesFromRequestScope_InFullCompositionRootUnderScopeValidation()
    {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ISystemPromptComposer>();

        act.Should().NotThrow(
            "the composer must resolve from a request scope without capturing scoped state from the root");
    }

    [Fact]
    public async Task ComposeAsync_TwoRequestScopesInFullRoot_EachSeesOwnConversationContext()
    {
        await using var provider = BuildProvider();

        // Turn 1: conversation conv-1, turn 3.
        using (var scope1 = provider.CreateScope())
        {
            scope1.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("agent-a", "conv-1", turnNumber: 3);

            var prompt1 = await scope1.ServiceProvider
                .GetRequiredService<ISystemPromptComposer>()
                .ComposeAsync("agent-a", tokenBudget: 10_000);

            prompt1.Should().Contain("Current turn: 3",
                "a host turn must compose against the CURRENT conversation's context, not a frozen one");
        }

        // Turn 2: a different conversation in a fresh scope must not see turn 1's state.
        using var scope2 = provider.CreateScope();
        scope2.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("agent-b", "conv-2", turnNumber: 7);

        var prompt2 = await scope2.ServiceProvider
            .GetRequiredService<ISystemPromptComposer>()
            .ComposeAsync("agent-b", tokenBudget: 10_000);

        prompt2.Should().Contain("Current turn: 7");
        prompt2.Should().NotContain("Current turn: 3",
            "session state must never bleed across request scopes through the full production graph");
    }
}
