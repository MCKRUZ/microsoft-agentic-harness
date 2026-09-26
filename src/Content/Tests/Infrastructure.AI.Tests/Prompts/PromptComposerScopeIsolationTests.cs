using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Services.Agent;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

/// <summary>
/// Regression tests for the captive-singleton prompt composer (audit finding H2).
/// The composer's section providers consume the scoped <see cref="IAgentExecutionContext"/>;
/// a singleton composer freezes the root-scope context at first resolution, so every
/// conversation composes against stale (or another request's) session state. These tests
/// bind to the production <see cref="PromptCompositionDependencyInjection"/> registrations
/// and run with <c>ValidateScopes</c> enabled — exactly what the AgentHub host had to
/// suppress to tolerate the captive dependency.
/// </summary>
public sealed class PromptComposerScopeIsolationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Production lifetime (Application.AI.Common.DependencyInjection): scoped ambient context.
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddSingleton(Mock.Of<IContextBudgetTracker>());

        // Production prompt composition registrations under test.
        services.AddSystemPromptComposition();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    [Fact]
    public async Task ComposeAsync_TwoRequestScopesWithDifferentContexts_EachSeesOwnSessionState()
    {
        using var provider = BuildProvider();

        // Request 1: conversation conv-1, turn 3.
        using (var scope1 = provider.CreateScope())
        {
            scope1.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("agent-a", "conv-1", turnNumber: 3);

            var prompt1 = await scope1.ServiceProvider
                .GetRequiredService<ISystemPromptComposer>()
                .ComposeAsync("agent-a", tokenBudget: 10_000);

            prompt1.Should().Contain("Current turn: 3",
                "the composer must see the live scope's execution context, not a captured root-scope instance");
            prompt1.Should().Contain("You are agent-a.");
        }

        // Request 2: a different conversation in a fresh scope must not see request 1's state.
        using var scope2 = provider.CreateScope();
        scope2.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("agent-b", "conv-2", turnNumber: 7);

        var prompt2 = await scope2.ServiceProvider
            .GetRequiredService<ISystemPromptComposer>()
            .ComposeAsync("agent-b", tokenBudget: 10_000);

        prompt2.Should().Contain("Current turn: 7");
        prompt2.Should().NotContain("Current turn: 3",
            "session state from another request scope must never bleed into this composition");
        prompt2.Should().Contain("You are agent-b.");
    }

    [Fact]
    public async Task ComposeAsync_SameConversationAcrossTurns_SeesFreshTurnNumber()
    {
        using var provider = BuildProvider();

        using (var turn1 = provider.CreateScope())
        {
            turn1.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("agent-a", "conv-1", turnNumber: 1);

            var prompt = await turn1.ServiceProvider
                .GetRequiredService<ISystemPromptComposer>()
                .ComposeAsync("agent-a", tokenBudget: 10_000);

            prompt.Should().Contain("Current turn: 1");
        }

        using var turn2 = provider.CreateScope();
        turn2.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("agent-a", "conv-1", turnNumber: 2);

        var prompt2 = await turn2.ServiceProvider
            .GetRequiredService<ISystemPromptComposer>()
            .ComposeAsync("agent-a", tokenBudget: 10_000);

        prompt2.Should().Contain("Current turn: 2",
            "each turn's scope must recompute non-cacheable sections from its own context");
        prompt2.Should().NotContain("Current turn: 1");
    }

    [Fact]
    public async Task ComposeAsync_CacheableIdentitySection_NotPoisonedByScopeContext()
    {
        using var provider = BuildProvider();

        // A scope bound to agent-b composes for cache key "agent-a" (e.g. a
        // supervisor composing a subagent's prompt). The cacheable identity
        // section is stored under the agent-a key, so its content must derive
        // from that key — not from the composing scope's execution context.
        using (var scope1 = provider.CreateScope())
        {
            scope1.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("agent-b", "conv-1", turnNumber: 1);

            var prompt = await scope1.ServiceProvider
                .GetRequiredService<ISystemPromptComposer>()
                .ComposeAsync("agent-a", tokenBudget: 10_000);

            prompt.Should().Contain("You are agent-a.");
            prompt.Should().NotContain("You are agent-b.",
                "the cacheable identity section must be a pure function of the cache key");
        }

        // A later scope composing for agent-a must get agent-a content from the
        // shared cache — not whatever identity the first scope happened to carry.
        using var scope2 = provider.CreateScope();
        scope2.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize("agent-a", "conv-2", turnNumber: 1);

        var prompt2 = await scope2.ServiceProvider
            .GetRequiredService<ISystemPromptComposer>()
            .ComposeAsync("agent-a", tokenBudget: 10_000);

        prompt2.Should().Contain("You are agent-a.");
        prompt2.Should().NotContain("You are agent-b.",
            "a cached section must never carry another conversation's identity");
    }

    [Fact]
    public void Composer_ResolvesFromRequestScope_UnderScopeValidation()
    {
        // Regression: the singleton registration forced the AgentHub host and its test
        // factories to disable ValidateScopes/ValidateOnBuild entirely, hiding every future
        // captive-dependency bug. The composer must resolve cleanly under scope validation.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ISystemPromptComposer>();

        act.Should().NotThrow();
    }
}
