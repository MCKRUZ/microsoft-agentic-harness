using FluentAssertions;
using Presentation.Common.Helpers;
using Xunit;

namespace Presentation.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="AppConfigHelper"/> covering environment detection,
/// manual config creation, and typed config retrieval.
/// </summary>
public sealed class AppConfigHelperTests
{
    // -- GetEnvironmentName --

    [Fact]
    public void GetEnvironmentName_WhenNotSet_ReturnsDevelopment()
    {
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
    public void GetEnvironmentName_WhenSet_ReturnsConfiguredValue()
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

    [Fact]
    public void GetEnvironmentName_EmptyString_ReturnsDevelopment()
    {
        var original = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "");

            var result = AppConfigHelper.GetEnvironmentName();

            result.Should().Be("Development");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", original);
        }
    }

    // -- CreateManualAppConfig --

    [Fact]
    public void CreateManualAppConfig_ReturnsNonNull()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Should().NotBeNull();
    }

    [Fact]
    public void CreateManualAppConfig_CommonSection_HasExpectedDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Common.ApplicationName.Should().Be("AgenticHarness");
        config.Common.ApplicationVersion.Should().Be("1.0");
        config.Common.SlowThresholdSec.Should().Be(5);
    }

    [Fact]
    public void CreateManualAppConfig_LoggingSection_HasExpectedDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Logging.PipeName.Should().Be("agentic-harness-logs");
        config.Logging.EnableStructuredJson.Should().BeTrue();
        config.Logging.RingBufferCapacity.Should().Be(500);
    }

    [Fact]
    public void CreateManualAppConfig_AgentSection_HasExpectedDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Agent.DefaultRequestTimeoutSec.Should().Be(30);
        config.Agent.DefaultTokenBudget.Should().Be(128_000);
    }

    [Fact]
    public void CreateManualAppConfig_HttpSection_HasExpectedDefaults()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Http.Authorization.Enabled.Should().BeFalse();
        config.Http.HttpSwagger.OpenApiEnabled.Should().BeFalse();
    }

    [Fact]
    public void CreateManualAppConfig_AllSectionsInstantiated()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Infrastructure.Should().NotBeNull();
        config.Connectors.Should().NotBeNull();
        config.Observability.Should().NotBeNull();
        config.AI.Should().NotBeNull();
        config.Azure.Should().NotBeNull();
        config.Cache.Should().NotBeNull();
    }

    [Fact]
    public void CreateManualAppConfig_ObservabilitySection_ContainsMcpServerProject()
    {
        var config = AppConfigHelper.CreateManualAppConfig();

        config.Observability.WebTelemetryProjects.Should().Contain("Infrastructure.AI.MCPServer");
    }
}
