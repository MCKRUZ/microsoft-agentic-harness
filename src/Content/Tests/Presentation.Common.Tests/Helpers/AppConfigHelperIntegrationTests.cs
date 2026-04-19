using FluentAssertions;
using Presentation.Common.Helpers;
using Xunit;

namespace Presentation.Common.Tests.Helpers;

/// <summary>
/// Integration tests for <see cref="AppConfigHelper"/> covering manual config
/// creation and environment detection.
/// </summary>
public sealed class AppConfigHelperIntegrationTests
{
    // -- CreateManualAppConfig --

    [Fact]
    public void CreateManualAppConfig_ReturnsNonNullConfig()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Should().NotBeNull();
    }

    [Fact]
    public void CreateManualAppConfig_SetsApplicationName()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Common.ApplicationName.Should().Be("AgenticHarness");
    }

    [Fact]
    public void CreateManualAppConfig_SetsApplicationVersion()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Common.ApplicationVersion.Should().Be("1.0");
    }

    [Fact]
    public void CreateManualAppConfig_SetsLoggingDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Logging.PipeName.Should().Be("agentic-harness-logs");
        config.Logging.EnableStructuredJson.Should().BeTrue();
        config.Logging.RingBufferCapacity.Should().Be(500);
    }

    [Fact]
    public void CreateManualAppConfig_SetsAgentDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Agent.DefaultRequestTimeoutSec.Should().Be(30);
        config.Agent.DefaultTokenBudget.Should().Be(128_000);
    }

    [Fact]
    public void CreateManualAppConfig_SetsCacheTypeToNone()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Cache.CacheType.Should().Be(Domain.Common.Config.Cache.CacheType.None);
    }

    [Fact]
    public void CreateManualAppConfig_SetsObservabilityDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Observability.WebTelemetryProjects.Should()
            .Contain("Infrastructure.AI.MCPServer");
    }

    [Fact]
    public void CreateManualAppConfig_InitializesAllNestedObjects()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Http.Should().NotBeNull();
        config.Infrastructure.Should().NotBeNull();
        config.Connectors.Should().NotBeNull();
        config.AI.Should().NotBeNull();
        config.Azure.Should().NotBeNull();
    }

    // -- GetEnvironmentName --

    [Fact]
    public void GetEnvironmentName_ReturnsNonNullString()
    {
        var result = AppConfigHelper.GetEnvironmentName();

        result.Should().NotBeNull();
    }

    [Fact]
    public void GetEnvironmentName_DefaultIsDevelopment()
    {
        // When ASPNETCORE_ENVIRONMENT is not set, returns "Development"
        var original = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            var result = AppConfigHelper.GetEnvironmentName();
            result.Should().Be("Development");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", original);
        }
    }

    [Fact]
    public void GetEnvironmentName_ReadsEnvironmentVariable()
    {
        var original = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Staging");
            var result = AppConfigHelper.GetEnvironmentName();
            result.Should().Be("Staging");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", original);
        }
    }
}
