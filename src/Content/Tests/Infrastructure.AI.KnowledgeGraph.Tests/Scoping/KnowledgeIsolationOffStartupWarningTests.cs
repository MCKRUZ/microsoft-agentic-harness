using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Scoping;

/// <summary>
/// <see cref="KnowledgeIsolationOffStartupWarning"/> (#904): a deployment running with knowledge isolation off, the
/// default, is told what its users share, and a deployment that turned it on is not.
/// </summary>
public sealed class KnowledgeIsolationOffStartupWarningTests
{
    [Fact]
    public void AddKnowledgeGraphDependencies_Always_RegistersIsolationWarning()
    {
        var config = new AppConfig();
        var services = new ServiceCollection();

        services.AddKnowledgeGraphDependencies(config);

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(KnowledgeIsolationOffStartupWarning));
    }

    [Theory]
    [InlineData(false, "Production", 1)]
    [InlineData(false, "Development", 0)]
    [InlineData(false, null, 1)]
    [InlineData(true, "Production", 0)]
    public async Task StartAsync_IsolationAndEnvironment_WarnsOnlyWhenOffOutsideDevelopment(
        bool isolation, string? environment, int warnings)
    {
        // Decided from the same setting the store's registration reads, read at startup. No host environment at all
        // (a composition without a host) is not Development, so it warns.
        var config = new AppConfig();
        config.AI.Rag.GraphRag.MultiTenantIsolation = isolation;
        var logger = new Mock<ILogger<KnowledgeIsolationOffStartupWarning>>();
        var warning = new KnowledgeIsolationOffStartupWarning(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            logger.Object,
            environment is null ? null : Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment));

        await warning.StartAsync(CancellationToken.None);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("MultiTenantIsolation is false")
                    && v.ToString()!.Contains("remembered facts stay private")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(warnings));
    }
}
