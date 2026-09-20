using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG;
using Infrastructure.AI.RAG.GraphRag;
using Infrastructure.AI.RAG.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// Regression tests for the two remote memory-hosting seams that live in
/// <c>Infrastructure.AI.RAG</c> (<see cref="ICrossSessionMemoryStore"/>,
/// <see cref="IMemoryDecayService"/>) — a different project from the other five seams because
/// that is where <c>AddRagCrossSessionMemory</c> owns their registration. Proves the override
/// takes effect regardless of <c>CrossSessionMemoryConfig.Enabled</c>, and that neither is
/// registered at all when both flags are off (the "dark by default config" contract the other
/// five seams get for free via <c>HarmonicMemory:Mode = Off</c>).
/// </summary>
public sealed class RemoteMemoryRagDiWiringTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoteMemoryEnabled_ResolvesRemoteImplementations_RegardlessOfCrossSessionMemoryConfig(
        bool crossSessionMemoryEnabled)
    {
        var config = new AppConfig();
        config.AI.RemoteMemory.Enabled = true;
        config.AI.Rag.CrossSessionMemory.Enabled = crossSessionMemoryEnabled;

        using var provider = BuildProvider(config);

        provider.GetRequiredService<ICrossSessionMemoryStore>().Should().BeOfType<RemoteCrossSessionMemoryStore>();
        provider.GetRequiredService<IMemoryDecayService>().Should().BeOfType<RemoteMemoryDecayService>();
    }

    [Fact]
    public void RemoteMemoryDisabled_CrossSessionMemoryDisabled_NeitherSeamIsRegistered()
    {
        var config = new AppConfig();
        config.AI.RemoteMemory.Enabled = false;
        config.AI.Rag.CrossSessionMemory.Enabled = false;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));
        services.AddRagDependencies(config);
        using var provider = services.BuildServiceProvider();

        provider.GetService<ICrossSessionMemoryStore>().Should().BeNull(
            "neither remote nor local cross-session memory is enabled, so nothing should resolve");
        provider.GetService<IMemoryDecayService>().Should().BeNull();
    }

    [Fact]
    public void RemoteMemoryDisabled_CrossSessionMemoryEnabled_ResolvesLocalImplementations()
    {
        var config = new AppConfig();
        config.AI.RemoteMemory.Enabled = false;
        config.AI.Rag.CrossSessionMemory.Enabled = true;
        config.AI.Rag.GraphDatabase.Enabled = true;
        config.AI.Rag.GraphDatabase.Provider = "kuzu";
        config.AI.Rag.GraphDatabase.DataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        using var provider = BuildProvider(config);

        provider.GetRequiredService<ICrossSessionMemoryStore>().Should().BeOfType<CrossSessionMemoryStore>();
        provider.GetRequiredService<IMemoryDecayService>().Should().BeOfType<MemoryDecayService>();
    }

    private static ServiceProvider BuildProvider(AppConfig config)
    {
        config.AI.Rag.GraphDatabase.Enabled = true;
        config.AI.Rag.GraphDatabase.Provider = "kuzu";
        if (string.IsNullOrEmpty(config.AI.Rag.GraphDatabase.DataDirectory))
            config.AI.Rag.GraphDatabase.DataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        // KuzuGraphBackend opens a real SQLite file eagerly in its constructor, so the mocked
        // IOwnerOnlyDirectoryCreator below (a no-op) is not enough — the directory must actually exist.
        Directory.CreateDirectory(config.AI.Rag.GraphDatabase.DataDirectory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));
        services.AddSingleton(Mock.Of<Application.Common.Interfaces.Common.IOwnerOnlyDirectoryCreator>());

        services.AddRagDependencies(config);

        return services.BuildServiceProvider();
    }
}
