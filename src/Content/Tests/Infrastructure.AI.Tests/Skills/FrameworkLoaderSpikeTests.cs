using FluentAssertions;
using Microsoft.Agents.AI;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Read-only spike: verify that <see cref="AgentSkillsProviderBuilder"/> from
/// Microsoft.Agents.AI can construct a skills provider with our existing skill paths.
/// In 1.4.0, <c>FileAgentSkillsProvider</c> was replaced by <see cref="AgentSkillsProviderBuilder"/>
/// which is the recommended public API for progressive skill disclosure.
/// </summary>
[Trait("Category", "Spike")]
public class FrameworkLoaderSpikeTests
{
    private static readonly AgentFileSkillScriptRunner NoOpRunner =
        (skill, script, arguments, serviceProvider, cancellationToken) =>
            Task.FromResult<object?>(null);

    private readonly string _skillsRoot;

    public FrameworkLoaderSpikeTests()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        _skillsRoot = Path.Combine(
            repoRoot,
            "src", "Content", "Application", "Application.Core", "Agents", "Skills");

        Directory.Exists(_skillsRoot).Should().BeTrue(
            $"Skills directory must exist at {_skillsRoot}");
    }

    [Fact]
    public void AgentSkillsProviderBuilder_ConstructionWithSkillPaths_DoesNotThrow()
    {
        var act = () => new AgentSkillsProviderBuilder()
            .UseFileSkill(_skillsRoot)
            .UseFileScriptRunner(NoOpRunner)
            .Build();

        act.Should().NotThrow(
            "AgentSkillsProviderBuilder should accept our skill paths without error");
    }

    [Fact]
    public void AgentSkillsProviderBuilder_ConstructedInstance_IsNotNull()
    {
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(_skillsRoot)
            .UseFileScriptRunner(NoOpRunner)
            .Build();

        provider.Should().NotBeNull();
    }

    [Fact]
    public void AgentSkillsProviderBuilder_WithMultiplePaths_ConstructsSuccessfully()
    {
        var echoPath = Path.Combine(_skillsRoot, "echo-test");
        var orchestratorPath = Path.Combine(_skillsRoot, "orchestrator-agent");
        var researchPath = Path.Combine(_skillsRoot, "research-agent");

        var builder = new AgentSkillsProviderBuilder()
            .UseFileScriptRunner(NoOpRunner);
        foreach (var path in new[] { echoPath, orchestratorPath, researchPath })
            builder.UseFileSkill(path);

        var provider = builder.Build();

        provider.Should().NotBeNull();
    }

    [Fact]
    public void AgentSkillsProviderBuilder_WithFilter_ConstructsSuccessfully()
    {
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(_skillsRoot)
            .UseFileScriptRunner(NoOpRunner)
            .UseFilter(s => s.Frontmatter.Name != "internal")
            .Build();

        provider.Should().NotBeNull();
    }

    [Fact]
    public void AgentSkillsProvider_IsAIContextProvider()
    {
        var provider = new AgentSkillsProviderBuilder()
            .UseFileSkill(_skillsRoot)
            .UseFileScriptRunner(NoOpRunner)
            .Build();

        provider.Should().BeAssignableTo<AIContextProvider>(
            "AgentSkillsProvider should be an AIContextProvider for agent wiring");
    }

    private static string FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "src", "AgenticHarness.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"Could not find repo root (containing src/AgenticHarness.slnx) starting from {startDir}");
    }
}
