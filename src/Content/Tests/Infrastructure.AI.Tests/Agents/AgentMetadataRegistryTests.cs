using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Tests for filesystem-based agent discovery via <see cref="AgentMetadataRegistry"/>.
/// Uses real AGENT.md files from the repo-root <c>agents/</c> directory where available;
/// when the directory is not present in the test-run environment the repo-facing tests
/// no-op to stay portable across CI and local runs.
/// </summary>
public sealed class AgentMetadataRegistryTests
{
    private static string RepoAgentsPath => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "agents"));

    private static AgentMetadataRegistry CreateRegistry(string? agentsPath = null)
        => CreateRegistry(new AgentOwnedSkillStore(), agentsPath);

    private static AgentMetadataRegistry CreateRegistry(IAgentOwnedSkillStore ownedSkills, string? agentsPath = null)
    {
        var resolvedPath = agentsPath ?? RepoAgentsPath;
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Agents = new AgentsConfig { BasePath = resolvedPath },
            },
        };
        return new AgentMetadataRegistry(
            NullLogger<AgentMetadataRegistry>.Instance,
            new OptionsMonitorStub(appConfig),
            new AgentMetadataParser(NullLogger<AgentMetadataParser>.Instance),
            new SkillMetadataParser(NullLogger<SkillMetadataParser>.Instance),
            ownedSkills);
    }

    [Fact]
    public void GetAll_WithValidAgentsPath_ReturnsDiscoveredAgents()
    {
        if (!Directory.Exists(RepoAgentsPath))
            return;

        var registry = CreateRegistry();

        var agents = registry.GetAll();

        agents.Should().NotBeEmpty("the repo-root agents/ directory seeds at least one AGENT.md");
    }

    [Fact]
    public void TryGet_DefaultAgent_ReturnsDefinition()
    {
        if (!Directory.Exists(RepoAgentsPath))
            return;

        var registry = CreateRegistry();

        var agent = registry.TryGet("default");

        agent.Should().NotBeNull();
        agent!.Id.Should().Be("default");
        agent.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGet_NonExistentAgent_ReturnsNull()
    {
        var registry = CreateRegistry(agentsPath: Path.GetTempPath() + "no-agents-here");

        var agent = registry.TryGet("does-not-exist");

        agent.Should().BeNull();
    }

    [Fact]
    public void GetAll_EmptyAgentsPath_ReturnsEmptyList()
    {
        var registry = CreateRegistry(agentsPath: Path.GetTempPath() + "no-agents-here");

        var agents = registry.GetAll();

        agents.Should().BeEmpty();
    }

    [Fact]
    public void GetAll_DiscoversMultipleAgentsInSeparateSubdirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                category: cat-a
                tags: ["one"]
                ---
                """);
            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                category: cat-b
                tags: ["two"]
                ---
                """);

            var registry = CreateRegistry(agentsPath: tempRoot);

            registry.GetAll().Should().HaveCount(2);
            registry.GetByCategory("cat-a").Select(a => a.Id).Should().ContainSingle(id => id == "alpha");
            registry.GetByTags(["two"]).Select(a => a.Id).Should().ContainSingle(id => id == "beta");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IAgentMetadataRegistry_IsRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(new AppConfig()));
        services.AddSingleton<AgentMetadataParser>();
        services.AddSingleton<SkillMetadataParser>();
        services.AddSingleton<IAgentOwnedSkillStore, AgentOwnedSkillStore>();
        services.AddSingleton<IAgentMetadataRegistry, AgentMetadataRegistry>();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<IAgentMetadataRegistry>();
        registry.Should().NotBeNull();
    }

    [Fact]
    public void GetAll_AgentWithNestedSkills_RegistersThemUnderThatAgentOnly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-nested-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                skills: [alpha-only]
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "alpha-only", "Alpha's private skill.");

            WriteAgent(tempRoot, "beta", """
                ---
                id: beta
                name: Beta
                ---
                """);

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().HaveCount(2); // triggers discovery

            // Resolvable only for its owning agent...
            store.TryGet("alpha", "alpha-only").Should().NotBeNull();
            // ...invisible to another agent...
            store.TryGet("beta", "alpha-only").Should().BeNull();
            // ...and it never enters the global registry surface (the store is separate by construction).
            store.GetForAgent("alpha").Select(s => s.Id).Should().ContainSingle(id => id == "alpha-only");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_AgentWithoutSkillsDirectory_RegistersNothing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-noskills-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "solo", """
                ---
                id: solo
                name: Solo
                ---
                """);

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            store.GetForAgent("solo").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_NestedSkillDirWithoutSkillMd_IsSkippedAndDoesNotAbortDiscovery()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-emptyskill-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            WriteAgent(tempRoot, "alpha", """
                ---
                id: alpha
                name: Alpha
                ---
                """);
            WriteNestedSkill(tempRoot, "alpha", "good", "A valid nested skill.");

            // A skills/ subdir with no SKILL.md must be skipped without aborting discovery of the valid one.
            Directory.CreateDirectory(Path.Combine(tempRoot, "alpha", "skills", "empty"));

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle();

            store.GetForAgent("alpha").Select(s => s.Id).Should().ContainSingle(id => id == "good");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetAll_DuplicateAgentId_KeepsFirstAndDoesNotMergeOwnedSkills()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agents-dup-{Guid.NewGuid():N}");
        var store = new AgentOwnedSkillStore();
        try
        {
            // Two agent directories declaring the same id, each with its own private nested skill.
            WriteAgent(tempRoot, "first", """
                ---
                id: dup
                name: First
                ---
                """);
            WriteNestedSkill(tempRoot, "first", "first-skill", "First's private skill.");

            WriteAgent(tempRoot, "second", """
                ---
                id: dup
                name: Second
                ---
                """);
            WriteNestedSkill(tempRoot, "second", "second-skill", "Second's private skill.");

            var registry = CreateRegistry(store, agentsPath: tempRoot);
            registry.GetAll().Should().ContainSingle(a => a.Id == "dup");

            // Only the winning (first-discovered) agent's owned skills are registered — the colliding
            // agent's private skills must not merge into the same id's namespace.
            var owned = store.GetForAgent("dup").Select(s => s.Id).ToList();
            owned.Should().ContainSingle();
            owned.Should().BeSubsetOf(["first-skill", "second-skill"]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void WriteAgent(string root, string folderName, string content)
    {
        var dir = Path.Combine(root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), content);
    }

    private static void WriteNestedSkill(string root, string agentFolder, string skillId, string body)
    {
        var dir = Path.Combine(root, agentFolder, "skills", skillId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            id: {skillId}
            name: {skillId}
            ---
            {body}
            """);
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
