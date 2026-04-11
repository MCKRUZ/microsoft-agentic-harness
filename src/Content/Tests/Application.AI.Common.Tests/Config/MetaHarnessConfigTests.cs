using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.AI.Common.Tests.Config;

public class MetaHarnessConfigTests
{
    private static MetaHarnessConfig ResolveDefaults()
    {
        var services = new ServiceCollection();
        services.Configure<MetaHarnessConfig>(_ => { });
        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<MetaHarnessConfig>>().Value;
    }

    /// <summary>Resolves MetaHarnessConfig via IOptions with no overrides — verifies all property defaults.</summary>
    [Fact]
    public void MetaHarnessConfig_DefaultBinding_PopulatesAllDefaults()
    {
        var config = ResolveDefaults();

        config.TraceDirectoryRoot.Should().Be("traces");
        config.MaxIterations.Should().Be(10);
        config.SearchSetSize.Should().Be(50);
        config.ScoreImprovementThreshold.Should().BeApproximately(0.01, 1e-10);
        config.AutoPromoteOnImprovement.Should().BeFalse();
        config.EvalTasksPath.Should().Be("eval-tasks");
        config.SeedCandidatePath.Should().Be("");
        config.MaxEvalParallelism.Should().Be(1);
        config.EvaluationTemperature.Should().BeApproximately(0.0, 1e-10);
        config.EvaluationModelVersion.Should().BeNull();
        config.SnapshotConfigKeys.Should().BeEmpty();
        config.SecretsRedactionPatterns.Should().ContainInOrder(
            "Key", "Secret", "Token", "Password", "ConnectionString");
        config.MaxFullPayloadKB.Should().Be(512);
        config.MaxRunsToKeep.Should().Be(20);
        config.EnableShellTool.Should().BeFalse();
        config.EnableMcpTraceResources.Should().BeTrue();
    }

    /// <summary>TraceDirectoryRoot defaults to "traces" when not present in config.</summary>
    [Fact]
    public void TraceDirectoryRoot_NotConfigured_DefaultsToTraces()
    {
        var config = ResolveDefaults();
        config.TraceDirectoryRoot.Should().Be("traces");
    }

    /// <summary>MaxIterations defaults to 10.</summary>
    [Fact]
    public void MaxIterations_NotConfigured_DefaultsToTen()
    {
        var config = ResolveDefaults();
        config.MaxIterations.Should().Be(10);
    }

    /// <summary>SecretsRedactionPatterns contains Key, Secret, Token, Password, ConnectionString.</summary>
    [Fact]
    public void SecretsRedactionPatterns_NotConfigured_ContainsExpectedDefaults()
    {
        var config = ResolveDefaults();
        config.SecretsRedactionPatterns.Should().ContainInOrder(
            "Key", "Secret", "Token", "Password", "ConnectionString");
    }

    /// <summary>EnableShellTool defaults to false.</summary>
    [Fact]
    public void EnableShellTool_NotConfigured_DefaultsToFalse()
    {
        var config = ResolveDefaults();
        config.EnableShellTool.Should().BeFalse();
    }

    /// <summary>MaxEvalParallelism defaults to 1.</summary>
    [Fact]
    public void MaxEvalParallelism_NotConfigured_DefaultsToOne()
    {
        var config = ResolveDefaults();
        config.MaxEvalParallelism.Should().Be(1);
    }
}
