using Domain.Common.Logging;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Logging;

/// <summary>
/// Tests for <see cref="FileLoggerOptions"/> default values and property mutation.
/// </summary>
public class FileLoggerOptionsTests
{
    [Fact]
    public void DefaultValues_AreNull()
    {
        var options = new FileLoggerOptions();

        options.LogsBasePath.Should().BeNull();
        options.CurrentRunId.Should().BeNull();
    }

    [Fact]
    public void LogsBasePath_CanBeSet()
    {
        var options = new FileLoggerOptions { LogsBasePath = "/var/logs" };

        options.LogsBasePath.Should().Be("/var/logs");
    }

    [Fact]
    public void CurrentRunId_CanBeSet()
    {
        var options = new FileLoggerOptions { CurrentRunId = "run-001" };

        options.CurrentRunId.Should().Be("run-001");
    }
}
