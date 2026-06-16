using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests;

/// <summary>
/// Regression tests for the composition-time coupling guard in
/// <see cref="DependencyInjection.AddKnowledgeGraphDependencies"/> that detects the silent
/// fail-open combination <c>MultiTenantIsolation=true</c> + <c>ComplianceEnabled=false</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TenantIsolatedGraphStore"/> only filters reads/writes; it never stamps
/// <see cref="Domain.AI.KnowledgeGraph.Models.GraphNode.TenantId"/> on write — that is delegated to
/// the inner <c>ComplianceAwareGraphStore</c>, which is registered only when
/// <c>ComplianceEnabled</c> is true. With compliance off, ingested corpus/learnings/skill nodes are
/// written with a null tenant and become cross-tenant-visible even though isolation is nominally on.
/// The guard surfaces this misconfiguration with a loud warning at first store resolution instead of
/// failing silent.
/// </para>
/// </remarks>
public sealed class DependencyInjectionTenantComplianceGuardTests
{
    private static (ServiceProvider provider, Mock<ILogger<TenantIsolatedGraphStore>> logger)
        BuildProvider(bool multiTenantIsolation, bool complianceEnabled)
    {
        var config = new AppConfig();
        config.AI.Rag.GraphRag.GraphProvider = "in_memory";
        config.AI.Rag.GraphRag.MultiTenantIsolation = multiTenantIsolation;
        config.AI.Rag.GraphRag.ComplianceEnabled = complianceEnabled;

        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config);
        var logger = new Mock<ILogger<TenantIsolatedGraphStore>>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor);
        services.AddKnowledgeGraphDependencies(config);
        // Override the typed logger so the guard's warning is observable. The factory resolves via
        // GetRequiredService, so this later registration wins for the closed generic.
        services.AddSingleton(logger.Object);

        return (services.BuildServiceProvider(), logger);
    }

    private static void VerifyWarningLogged(Mock<ILogger<TenantIsolatedGraphStore>> logger, Times times)
    {
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ComplianceEnabled is false")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }

    [Fact]
    public void ResolveStore_IsolationOnComplianceOff_LogsCrossTenantExposureWarning()
    {
        var (provider, logger) = BuildProvider(multiTenantIsolation: true, complianceEnabled: false);

        // Resolution triggers the lazy singleton factory that builds the decorator chain.
        _ = provider.GetRequiredService<IKnowledgeGraphStore>();

        VerifyWarningLogged(logger, Times.Once());
    }

    [Fact]
    public void ResolveStore_IsolationOnComplianceOn_DoesNotLogWarning()
    {
        var (provider, logger) = BuildProvider(multiTenantIsolation: true, complianceEnabled: true);

        _ = provider.GetRequiredService<IKnowledgeGraphStore>();

        VerifyWarningLogged(logger, Times.Never());
    }

    [Fact]
    public void ResolveStore_IsolationOff_DoesNotLogWarning()
    {
        var (provider, logger) = BuildProvider(multiTenantIsolation: false, complianceEnabled: false);

        _ = provider.GetRequiredService<IKnowledgeGraphStore>();

        VerifyWarningLogged(logger, Times.Never());
    }
}
