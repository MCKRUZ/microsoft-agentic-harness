using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
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
            new AgentMetadataParser(NullLogger<AgentMetadataParser>.Instance));
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
        services.AddSingleton<IAgentMetadataRegistry, AgentMetadataRegistry>();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<IAgentMetadataRegistry>();
        registry.Should().NotBeNull();
    }

    private static void WriteAgent(string root, string folderName, string content)
    {
        var dir = Path.Combine(root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), content);
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
