using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.GraphRag;

/// <summary>
/// Composition tests for the GraphRag registration gate. <c>IGraphRagService</c> requires the
/// <c>IGraphDatabaseBackend</c> that only exists while <c>GraphDatabase.Enabled</c> is true,
/// so its registration (and the keyed <c>"graph"</c> retrieval source that wraps it) must
/// follow the same gate. Before this gate, a disabled backend composed a registered-but-broken
/// singleton that threw on the first <c>RagOrchestrator</c> resolution — invisible to
/// ValidateOnBuild because the factory hid the dependency.
/// </summary>
public sealed class GraphRagDiRegistrationTests
{
    private static ServiceCollection BuildRagServices(Action<AppConfig>? configure = null)
    {
        var appConfig = new AppConfig();
        configure?.Invoke(appConfig);

        var services = new ServiceCollection();
        services.AddRagDependencies(appConfig);
        return services;
    }

    [Fact]
    public void AddRagDependencies_GraphDatabaseDisabled_DoesNotRegisterGraphServices()
    {
        var services = BuildRagServices(c => c.AI.Rag.GraphDatabase.Enabled = false);

        services.Should().NotContain(
            d => !d.IsKeyedService && d.ServiceType == typeof(IGraphRagService),
            "IGraphRagService requires the graph database backend and must follow its gate");
        services.Should().NotContain(
            d => !d.IsKeyedService && d.ServiceType == typeof(IGraphDatabaseBackend));
        services.Should().NotContain(
            d => d.IsKeyedService
                 && d.ServiceType == typeof(IRetrievalSource)
                 && Equals(d.ServiceKey, "graph"),
            "the keyed graph retrieval source wraps IGraphRagService and must follow the same gate");
    }

    [Fact]
    public void AddRagDependencies_GraphDatabaseDisabled_GraphServicesResolveNullNotThrow()
    {
        var services = BuildRagServices(c => c.AI.Rag.GraphDatabase.Enabled = false);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IGraphRagService>().Should().BeNull();
        provider.GetKeyedService<IRetrievalSource>("graph").Should().BeNull(
            "MultiSourceOrchestrator logs-and-skips a null keyed source, so absence degrades " +
            "gracefully instead of throwing per fan-out");
    }

    [Fact]
    public void AddRagDependencies_GraphDatabaseEnabled_RegistersGraphServices()
    {
        // GraphDatabaseConfig.Enabled defaults to true with Provider "kuzu".
        var services = BuildRagServices();

        services.Should().Contain(
            d => !d.IsKeyedService && d.ServiceType == typeof(IGraphRagService));
        services.Should().Contain(
            d => !d.IsKeyedService && d.ServiceType == typeof(IGraphDatabaseBackend));
        services.Should().Contain(
            d => d.IsKeyedService
                 && d.ServiceType == typeof(IGraphDatabaseBackend)
                 && Equals(d.ServiceKey, "kuzu"));
        services.Should().Contain(
            d => d.IsKeyedService
                 && d.ServiceType == typeof(IRetrievalSource)
                 && Equals(d.ServiceKey, "graph"));
    }
}
