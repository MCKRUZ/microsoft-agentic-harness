using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for filesystem-based skill discovery via <see cref="SkillMetadataRegistry"/>.
/// Uses real SKILL.md files from the top-level skills/ directory.
/// </summary>
public sealed class SkillMetadataRegistryTests
{
    private static string SkillsPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "skills"));

    private static SkillMetadataRegistry CreateRegistry(string? skillsPath = null)
    {
        var resolvedPath = skillsPath ?? SkillsPath;
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Skills = new SkillsConfig { BasePath = resolvedPath }
            }
        };
        var optionsMonitor = new OptionsMonitorStub(appConfig);
        var logger = NullLogger<SkillMetadataRegistry>.Instance;
        var parser = new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance);

        return new SkillMetadataRegistry(logger, optionsMonitor, parser);
    }

    [Fact]
    public void GetAll_WithValidSkillsPath_ReturnsDiscoveredSkills()
    {
        if (!Directory.Exists(SkillsPath))
            return; // Skills dir not present in this test run environment — skip

        var registry = CreateRegistry();

        var skills = registry.GetAll();

        skills.Should().NotBeEmpty("skills/ directory has SKILL.md files");
    }

    [Fact]
    public void TryGet_ResearchAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("research-agent");

        skill.Should().NotBeNull();
        skill!.Id.Should().Be("research-agent");
        skill.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGet_OrchestratorAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("orchestrator-agent");

        skill.Should().NotBeNull();
        skill!.Id.Should().Be("orchestrator-agent");
        skill.Category.Should().Be("orchestration");
    }

    [Fact]
    public void TryGet_NonExistentSkill_ReturnsNull()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("does-not-exist");

        skill.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_Research_ReturnsResearchSkills()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skills = registry.GetByCategory("research");

        skills.Should().Contain(s => s.Id == "research-agent");
    }

    [Fact]
    public void GetByTags_Orchestrator_ReturnsOrchestratorSkill()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skills = registry.GetByTags(["orchestrator"]);

        skills.Should().Contain(s => s.Id == "orchestrator-agent");
    }

    [Fact]
    public void GetAll_EmptySkillsPath_ReturnsEmptyList()
    {
        var registry = CreateRegistry(skillsPath: Path.GetTempPath() + "no-skills-here");

        var skills = registry.GetAll();

        skills.Should().BeEmpty();
    }

    [Fact]
    public void ISkillMetadataRegistry_IsRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(new AppConfig()));
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<ISkillMetadataRegistry, SkillMetadataRegistry>();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<ISkillMetadataRegistry>();
        registry.Should().NotBeNull();
    }

    [Fact]
    public void SkillMetadataRegistry_IncludesObjectives_InReturnedSkillDefinition()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        var skill = registry.TryGet("harness-proposer");

        skill.Should().NotBeNull("harness-proposer SKILL.md includes ## Objectives");
        skill!.Objectives.Should().NotBeNullOrWhiteSpace();
        skill.HasObjectives.Should().BeTrue();
    }

    [Fact]
    public void SkillMetadataRegistry_ExistingSkillsWithoutNewSections_ParseCorrectly()
    {
        if (!Directory.Exists(SkillsPath))
            return;

        var registry = CreateRegistry();

        // orchestrator-agent has no ## Objectives or ## Trace Format — should parse without error
        var skill = registry.TryGet("orchestrator-agent");

        skill.Should().NotBeNull();
        skill!.Objectives.Should().BeNull();
        skill.TraceFormat.Should().BeNull();
        skill.Instructions.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
