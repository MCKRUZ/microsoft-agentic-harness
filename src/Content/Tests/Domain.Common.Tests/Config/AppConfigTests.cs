using Domain.Common.Config;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Config;

/// <summary>
/// Tests for <see cref="AppConfig"/>, <see cref="CommonConfig"/>,
/// <see cref="AgentConfig"/>, and <see cref="LoggingConfig"/> default values.
/// </summary>
public class AppConfigTests
{
    [Fact]
    public void DefaultValues_AllSubsectionsInitialized()
    {
        var config = new AppConfig();

        config.Common.Should().NotBeNull();
        config.Logging.Should().NotBeNull();
        config.Agent.Should().NotBeNull();
        config.Http.Should().NotBeNull();
        config.Infrastructure.Should().NotBeNull();
        config.Connectors.Should().NotBeNull();
        config.Observability.Should().NotBeNull();
        config.AI.Should().NotBeNull();
        config.Azure.Should().NotBeNull();
        config.Cache.Should().NotBeNull();
        config.MetaHarness.Should().NotBeNull();
    }
}

/// <summary>
/// Tests for <see cref="CommonConfig"/> default values.
/// </summary>
public class CommonConfigTests
{
    [Fact]
    public void ApplicationName_DefaultsToAgenticHarness()
    {
        var config = new CommonConfig();

        config.ApplicationName.Should().Be("AgenticHarness");
    }

    [Fact]
    public void ApplicationVersion_DefaultsToOnePointZero()
    {
        var config = new CommonConfig();

        config.ApplicationVersion.Should().Be("1.0");
    }

    [Fact]
    public void SlowThresholdSec_DefaultsToFive()
    {
        var config = new CommonConfig();

        config.SlowThresholdSec.Should().Be(5);
    }
}

/// <summary>
/// Tests for <see cref="AgentConfig"/> default values.
/// </summary>
public class AgentConfigTests
{
    [Fact]
    public void DefaultRequestTimeoutSec_DefaultsToThirty()
    {
        var config = new AgentConfig();

        config.DefaultRequestTimeoutSec.Should().Be(30);
    }

    [Fact]
    public void DefaultTokenBudget_DefaultsTo128K()
    {
        var config = new AgentConfig();

        config.DefaultTokenBudget.Should().Be(128_000);
    }
}

/// <summary>
/// Tests for <see cref="LoggingConfig"/> default values.
/// </summary>
public class LoggingConfigTests
{
    [Fact]
    public void LogsBasePath_DefaultsToNull()
    {
        var config = new LoggingConfig();

        config.LogsBasePath.Should().BeNull();
    }

    [Fact]
    public void PipeName_DefaultsCorrectly()
    {
        var config = new LoggingConfig();

        config.PipeName.Should().Be("agentic-harness-logs");
    }

    [Fact]
    public void EnableStructuredJson_DefaultsTrue()
    {
        var config = new LoggingConfig();

        config.EnableStructuredJson.Should().BeTrue();
    }

    [Fact]
    public void RingBufferCapacity_DefaultsTo500()
    {
        var config = new LoggingConfig();

        config.RingBufferCapacity.Should().Be(500);
    }

    [Fact]
    public void SuppressConsoleOutput_DefaultsFalse()
    {
        var config = new LoggingConfig();

        config.SuppressConsoleOutput.Should().BeFalse();
    }
}
