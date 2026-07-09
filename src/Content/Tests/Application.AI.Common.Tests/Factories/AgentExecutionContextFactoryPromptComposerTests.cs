using Application.AI.Common.Factories;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests for wiring the authoritative <c>ISystemPromptComposer</c> onto the live agent-context
/// build path in <see cref="AgentExecutionContextFactory"/>, behind the default-off
/// <c>PromptComposition</c> flag. Covers: the ON path composing skill instructions into the static
/// prompt, the OFF path staying byte-identical to the legacy merged instruction, budget bounding
/// (via the composer), and fail-open when the composer cannot be resolved.
/// </summary>
public sealed class AgentExecutionContextFactoryPromptComposerTests
{
    private static IOptionsMonitor<AppConfig> Config(bool promptCompositionEnabled, int tokenBudget = 8000)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI,
                    DefaultDeployment = "default-model",
                    ApiKey = "test-key",
                    Endpoint = "https://test.example.com",
                },
                ContextManagement = new ContextManagementConfig
                {
                    PromptComposition = new PromptCompositionConfig
                    {
                        Enabled = promptCompositionEnabled,
                        TokenBudget = tokenBudget,
                    },
                },
            },
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
    }

    private static AgentExecutionContextFactory CreateFactory(
        IOptionsMonitor<AppConfig> config,
        IServiceProvider serviceProvider) =>
        new(
            NullLogger<AgentExecutionContextFactory>.Instance,
            config,
            serviceProvider,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, serviceProvider),
            new SkillPrerequisiteResolver());

    /// <summary>
    /// Builds a root provider containing the full prompt-composition graph plus the ambient request
    /// scope bridge, exactly as the composition root wires them.
    /// </summary>
    private static ServiceProvider BuildCompositionProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddSingleton(Mock.Of<IContextBudgetTracker>());
        services.AddSingleton<IAmbientRequestScope, AmbientRequestScope>();
        services.AddSystemPromptComposition();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static SkillDefinition Skill(string name, string instructions) =>
        new() { Id = name, Name = name, Instructions = instructions };

    // --- ON: the composer becomes authoritative and includes the skill instructions ---

    [Fact]
    public async Task MapToAgentContext_PromptCompositionEnabled_ComposedPromptContainsSkillInstructions()
    {
        await using var root = BuildCompositionProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: true), root);
        var skill = Skill("research", "SEARCH AND SYNTHESIZE SOURCES.");

        var ambient = root.GetRequiredService<IAmbientRequestScope>();
        using var scope = root.CreateScope();
        using (ambient.BeginScope(scope.ServiceProvider))
        {
            var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

            // Proof the composer is authoritative — not just identity/permissions, the actual skill body.
            context.Instruction.Should().Contain("SEARCH AND SYNTHESIZE SOURCES.");
            // And the composer reframes it with the identity section (the delta vs the legacy path).
            context.Instruction.Should().Contain("You are ResearchAgent.");
        }
    }

    // --- OFF: byte-identical to the legacy merged instruction ---

    [Fact]
    public async Task MapToAgentContext_PromptCompositionDisabled_InstructionMatchesExactLegacyFormat()
    {
        await using var root = BuildCompositionProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: false), root);
        var skills = new List<SkillDefinition>
        {
            Skill("Research", "Search sources."),
            Skill("Present", "Make slides."),
        };
        var options = new SkillAgentOptions { AdditionalContext = "Extra context." };

        var context = await factory.MapToAgentContextAsync(skills, options);

        // Hardcoded expected format (multi-skill => "## Skill: {name}\n\n{instructions}" wrapping,
        // AdditionalContext appended, blocks joined by a blank line). Pinned literally so a future
        // change to the merge format is caught, not silently mirrored.
        const string expected =
            "## Skill: Research\n\nSearch sources.\n\n" +
            "## Skill: Present\n\nMake slides.\n\n" +
            "Extra context.";
        context.Instruction.Should().Be(expected);
        // The legacy path must NOT carry the composer's identity framing.
        context.Instruction.Should().NotContain("You are ");
    }

    [Fact]
    public async Task MapToAgentContext_SingleSkill_DisabledInstructionIsVerbatim()
    {
        await using var root = BuildCompositionProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: false), root);

        var context = await factory.MapToAgentContextAsync(
            Skill("Research", "Search sources."), new SkillAgentOptions());

        // Single skill, no additional context => instructions used verbatim, no header.
        context.Instruction.Should().Be("Search sources.");
    }

    // --- Budget MUST NOT silently drop the core skill instructions (ON path) ---

    [Fact]
    public async Task MapToAgentContext_EnabledWithTinyBudget_StillContainsSkillInstructions()
    {
        // A pathologically small budget (1 token) cannot fit the skill body. The composer must
        // degrade to a prompt that STILL contains the skill instructions — never one without them.
        await using var root = BuildCompositionProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: true, tokenBudget: 1), root);
        var longBody = string.Join(" ", Enumerable.Repeat("SYNTHESIZE-SOURCES-CAREFULLY", 40));
        var skill = Skill("research", longBody);

        var ambient = root.GetRequiredService<IAmbientRequestScope>();
        using var scope = root.CreateScope();
        using (ambient.BeginScope(scope.ServiceProvider))
        {
            var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

            context.Instruction.Should().Contain(longBody,
                "the agent's core job description must survive even a too-small token budget");
        }
    }

    [Fact]
    public async Task MapToAgentContext_DisabledVsEnabled_ProduceDifferentInstructions()
    {
        await using var root = BuildCompositionProvider();
        var skill = Skill("research", "Search sources.");

        var offContext = await CreateFactory(Config(false), root)
            .MapToAgentContextAsync(skill, new SkillAgentOptions());

        var ambient = root.GetRequiredService<IAmbientRequestScope>();
        using var scope = root.CreateScope();
        string? onInstruction;
        using (ambient.BeginScope(scope.ServiceProvider))
        {
            var onContext = await CreateFactory(Config(true), root)
                .MapToAgentContextAsync(skill, new SkillAgentOptions());
            onInstruction = onContext.Instruction;
        }

        offContext.Instruction.Should().Be("Search sources.");
        onInstruction.Should().Contain("Search sources.");
        onInstruction.Should().Contain("You are ResearchAgent.");
        onInstruction.Should().NotBe(offContext.Instruction);
    }

    // --- Fail-open: composer/scope unavailable while ON → legacy instruction, no throw ---

    [Fact]
    public async Task MapToAgentContext_EnabledButNoAmbientScope_FailsOpenToLegacyInstruction()
    {
        // A service provider WITHOUT IAmbientRequestScope registered — the factory cannot reach a
        // request scope, so it must fall back to the legacy instruction rather than throw.
        await using var barren = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: true), barren);
        var skill = Skill("research", "Search sources.");

        var act = async () => await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        var context = await act.Should().NotThrowAsync();
        context.Subject.Instruction.Should().Be("Search sources.",
            "with no request scope the composer cannot run and the legacy merged instruction is used verbatim");
    }

    [Fact]
    public async Task MapToAgentContext_EnabledButScopeNotEntered_FailsOpenToLegacyInstruction()
    {
        // IAmbientRequestScope is registered but no BeginScope has been called, so Current is null.
        await using var root = BuildCompositionProvider();
        var factory = CreateFactory(Config(promptCompositionEnabled: true), root);
        var skill = Skill("research", "Search sources.");

        var context = await factory.MapToAgentContextAsync(skill, new SkillAgentOptions());

        context.Instruction.Should().Be("Search sources.");
    }
}
