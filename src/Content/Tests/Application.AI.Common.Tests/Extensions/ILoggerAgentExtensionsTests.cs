using Application.AI.Common.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// Tests for <see cref="ILoggerAgentExtensions"/> covering tool execution logging,
/// agent turn logging, content safety events, MCP operations, and agent events.
/// </summary>
public class ILoggerAgentExtensionsTests
{
    private readonly Mock<ILogger> _logger = new();

    [Fact]
    public void LogToolExecution_Success_LogsAtInformation()
    {
        _logger.Object.LogToolExecution("file_system", TimeSpan.FromMilliseconds(50), succeeded: true, "read ok");

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("file_system")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogToolExecution_Failure_LogsAtWarning()
    {
        _logger.Object.LogToolExecution("web_fetch", TimeSpan.FromSeconds(5), succeeded: false, "timeout");

        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("web_fetch")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogToolExecution_NullSummary_LogsNAPlaceholder()
    {
        _logger.Object.LogToolExecution("tool", TimeSpan.Zero, succeeded: true);

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("N/A")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogAgentTurn_LogsAtInformation()
    {
        _logger.Object.LogAgentTurn("planner", 3, tokensUsed: 5000);

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("planner") && o.ToString()!.Contains("3")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogAgentTurn_NullTokens_LogsNAPlaceholder()
    {
        _logger.Object.LogAgentTurn("agent", 1);

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("N/A")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogContentSafetyEvent_BlockedAction_LogsAtWarning()
    {
        _logger.Object.LogContentSafetyEvent("violence", "blocked", "harmful content detected");

        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("violence")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogContentSafetyEvent_AllowedAction_LogsAtInformation()
    {
        _logger.Object.LogContentSafetyEvent("pii", "allowed", "no PII detected");

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("pii")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogMcpOperation_LogsAtInformation()
    {
        _logger.Object.LogMcpOperation("inbound", "tool_list", "local-tools", TimeSpan.FromMilliseconds(100));

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("inbound") && o.ToString()!.Contains("local-tools")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogMcpOperation_NullServerAndDuration_LogsNAPlaceholders()
    {
        _logger.Object.LogMcpOperation("outbound", "invoke");

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("N/A")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogAgentEvent_WithData_LogsAtInformation()
    {
        var data = new Dictionary<string, object> { ["key"] = "value" };
        _logger.Object.LogAgentEvent("skill_loaded", data, "planner");

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("skill_loaded")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogAgentEvent_NullDataAndContext_LogsNAPlaceholders()
    {
        _logger.Object.LogAgentEvent("event");

        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("N/A")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
