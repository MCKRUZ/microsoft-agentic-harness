using Application.AI.Common.Factories;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Factories;

/// <summary>
/// Tests that the agent tool ceiling declared on <see cref="SkillAgentOptions.AllowedTools"/> reaches
/// the runtime <see cref="ToolPermissionFilter"/> as the intersection with the skills' combined
/// allowlist. Complements <c>ToolCeilingResolverTests</c> (which proves the tighten-only invariant on
/// the pure primitive) by proving the factory actually wires that invariant onto the agent.
/// </summary>
public sealed class AgentExecutionContextFactoryToolCeilingTests
{
    private readonly AgentExecutionContextFactory _factory;

    public AgentExecutionContextFactoryToolCeilingTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    DefaultDeployment = "gpt-4o",
                    ClientType = AIAgentFrameworkClientType.AzureOpenAI
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var sp = new ServiceCollection().BuildServiceProvider();

        _factory = new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp),
            new SkillPrerequisiteResolver());
    }

    private static SkillDefinition Skill(string id, params string[] allowedTools) => new()
    {
        Id = id,
        Name = id,
        Instructions = $"Instructions for {id}",
        AllowedTools = allowedTools.Length > 0 ? allowedTools : null
    };

    private static ToolPermissionFilter? GetFilter(Domain.AI.Agents.AgentExecutionContext context) =>
        context.AIContextProviders?.OfType<ToolPermissionFilter>().SingleOrDefault();

    [Fact]
    public async Task Ceiling_NarrowsSkillUnion_FilterHoldsIntersection()
    {
        var skills = new[] { Skill("s", "read", "write", "delete") };
        var options = new SkillAgentOptions { AllowedTools = ["read", "write"] };

        var context = await _factory.MapToAgentContextAsync(skills, options);

        GetFilter(context)!.AllowedTools.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public async Task Ceiling_NamesToolSkillsDoNotGrant_FilterNeverWidens()
    {
        var skills = new[] { Skill("s", "read", "write") };
        var options = new SkillAgentOptions { AllowedTools = ["read", "write", "delete"] };

        var context = await _factory.MapToAgentContextAsync(skills, options);

        var filter = GetFilter(context)!;
        filter.AllowedTools.Should().BeEquivalentTo(["read", "write"]);
        filter.AllowedTools.Should().NotContain("delete");
    }

    [Fact]
    public async Task Ceiling_OnSkillThatDeclaredNoRestriction_CapsToCeiling()
    {
        // The skill imposes no allowlist (all tools flow through). Declaring an agent ceiling must still
        // cap the agent — a ToolPermissionFilter appears where there was none, holding exactly the ceiling.
        var skills = new[] { Skill("open") };
        var options = new SkillAgentOptions { AllowedTools = ["read"] };

        var context = await _factory.MapToAgentContextAsync(skills, options);

        GetFilter(context)!.AllowedTools.Should().BeEquivalentTo(["read"]);
    }

    [Fact]
    public async Task NoCeiling_WithSkillAllowlist_FilterEqualsSkillUnion()
    {
        // Backward compatibility: with no agent ceiling the filter is exactly the skills' union.
        var skills = new[] { Skill("a", "read"), Skill("b", "write") };
        var options = new SkillAgentOptions();

        var context = await _factory.MapToAgentContextAsync(skills, options);

        GetFilter(context)!.AllowedTools.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public async Task NoCeiling_SkillsUnrestricted_NoFilterWired()
    {
        // Backward compatibility: no ceiling and no skill allowlist means no ToolPermissionFilter at all.
        var skills = new[] { Skill("open") };
        var options = new SkillAgentOptions();

        var context = await _factory.MapToAgentContextAsync(skills, options);

        GetFilter(context).Should().BeNull();
    }

    [Fact]
    public async Task Ceiling_DisjointFromSkillUnion_FilterIsEmptyAndStripsEverything()
    {
        var skills = new[] { Skill("s", "read", "write") };
        var options = new SkillAgentOptions { AllowedTools = ["deploy"] };

        var context = await _factory.MapToAgentContextAsync(skills, options);

        // A ceiling disjoint from what the skills grant leaves an empty intersection. The filter is still
        // wired (the agent asked for a ceiling) but permits nothing from the skills' set.
        GetFilter(context)!.AllowedTools.Should().BeEmpty();
    }
}
