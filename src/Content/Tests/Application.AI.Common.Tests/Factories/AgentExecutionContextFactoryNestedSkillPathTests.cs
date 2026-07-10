using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces.Skills;
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
/// Tests <see cref="AgentExecutionContextFactory"/>'s file-skill path resolution: an agent-owned nested
/// skill's own directory (outside the configured skill roots) is added so its Tier 2/3 content is
/// disclosable, while a global skill already under a configured root is not double-added. This pins the
/// augment-not-replace behaviour that keeps a mixed owned+shared agent from losing disclosure for its
/// shared skills.
/// </summary>
public sealed class AgentExecutionContextFactoryNestedSkillPathTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configSkillRoot;

    public AgentExecutionContextFactoryNestedSkillPathTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nestedpath-{Guid.NewGuid():N}");
        _configSkillRoot = Path.Combine(_tempRoot, "skills-config");
        Directory.CreateDirectory(_configSkillRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private AgentExecutionContextFactory CreateFactory()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { DefaultDeployment = "gpt-4o" },
                Skills = new SkillsConfig { BasePath = _configSkillRoot },
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);
        var sp = new ServiceCollection().BuildServiceProvider();

        return new AgentExecutionContextFactory(
            NullLogger<AgentExecutionContextFactory>.Instance,
            monitor,
            sp,
            NullLoggerFactory.Instance,
            new ToolChainBuilder(NullLogger<ToolChainBuilder>.Instance, sp),
            new SkillPrerequisiteResolver());
    }

    private string MakeDir(string relative)
    {
        var dir = Path.Combine(_tempRoot, relative);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ResolveSkillPaths_AgentOwnedSkillOutsideConfigRoots_AddsItsDirectory()
    {
        // Nested skill lives under an agent dir, outside the configured skill root.
        var ownedDir = MakeDir(Path.Combine("agents", "alpha", "skills", "nested"));
        var skill = new SkillDefinition { Id = "nested", Name = "nested", BaseDirectory = ownedDir };

        var paths = CreateFactory().ResolveSkillPaths(new SkillAgentOptions(), [skill]);

        paths.Should().Contain(p => Path.GetFullPath(p) == Path.GetFullPath(_configSkillRoot));
        paths.Should().Contain(p => Path.GetFullPath(p) == Path.GetFullPath(ownedDir));
    }

    [Fact]
    public void ResolveSkillPaths_GlobalSkillUnderConfigRoot_IsNotDoubleAdded()
    {
        // Global skill's directory sits under the configured root — it is already reachable.
        var globalDir = MakeDir(Path.Combine("skills-config", "research"));
        var skill = new SkillDefinition { Id = "research", Name = "research", BaseDirectory = globalDir };

        var paths = CreateFactory().ResolveSkillPaths(new SkillAgentOptions(), [skill]);

        paths.Where(p => Path.GetFullPath(p) == Path.GetFullPath(globalDir)).Should().BeEmpty();
        paths.Should().ContainSingle(p => Path.GetFullPath(p) == Path.GetFullPath(_configSkillRoot));
    }

    [Fact]
    public void ResolveSkillPaths_MixedOwnedAndShared_KeepsConfigRootAndAddsOnlyOwned()
    {
        var globalDir = MakeDir(Path.Combine("skills-config", "shared"));
        var ownedDir = MakeDir(Path.Combine("agents", "alpha", "skills", "private"));
        var shared = new SkillDefinition { Id = "shared", Name = "shared", BaseDirectory = globalDir };
        var owned = new SkillDefinition { Id = "private", Name = "private", BaseDirectory = ownedDir };

        var paths = CreateFactory().ResolveSkillPaths(new SkillAgentOptions(), [shared, owned]);

        // Config root present (so the shared skill discloses), owned dir added, global dir not re-added.
        paths.Should().Contain(p => Path.GetFullPath(p) == Path.GetFullPath(_configSkillRoot));
        paths.Should().Contain(p => Path.GetFullPath(p) == Path.GetFullPath(ownedDir));
        paths.Where(p => Path.GetFullPath(p) == Path.GetFullPath(globalDir)).Should().BeEmpty();
    }

    [Fact]
    public void ResolveSkillPaths_NoSkills_ReturnsOnlyConfigRoots()
    {
        var paths = CreateFactory().ResolveSkillPaths(new SkillAgentOptions(), []);

        paths.Should().ContainSingle(p => Path.GetFullPath(p) == Path.GetFullPath(_configSkillRoot));
    }
}
