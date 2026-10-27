using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests;

/// <summary>
/// <see cref="KnowledgeIsolationOffStartupWarning"/> (#904): a deployment running with knowledge isolation off, the
/// default, is told what its users share, and a deployment that turned it on is not.
/// </summary>
public sealed class KnowledgeIsolationOffStartupWarningTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TheWarning_IsRegisteredOnlyWhenIsolationIsOff(bool isolation, bool registered)
    {
        var config = new AppConfig();
        config.AI.Rag.GraphRag.MultiTenantIsolation = isolation;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config));

        services.AddKnowledgeGraphDependencies(config);

        services.Any(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(KnowledgeIsolationOffStartupWarning))
            .Should().Be(registered);
    }

    [Theory]
    [InlineData("Production", 1)]
    [InlineData("Development", 0)]
    [InlineData(null, 1)]
    public async Task TheWarning_IsLoggedOutsideDevelopment(string? environment, int warnings)
    {
        // No host environment at all (a composition without a host) is not Development, so it warns.
        var logger = new Mock<ILogger<KnowledgeIsolationOffStartupWarning>>();
        var warning = new KnowledgeIsolationOffStartupWarning(
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
