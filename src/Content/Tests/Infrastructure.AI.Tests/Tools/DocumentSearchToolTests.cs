using Application.AI.Common.Interfaces.RAG;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="DocumentSearchTool"/> covering the rejected-argument logging path added by #466.
/// </summary>
public sealed class DocumentSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_MissingQuery_LogsWarning_AndReturnsSafeFailureText()
    {
        // #466: DocumentSearchTool had no logger at all, so a rejected search argument left no
        // compensating detail once the exception message was replaced with the type-name-only
        // SafeFailureText. This proves both halves of the fix: the real exception detail is still
        // recoverable from structured logs, and the model-facing text never carries the raw message.
        var logger = new Mock<ILogger<DocumentSearchTool>>();
        var sut = new DocumentSearchTool(Mock.Of<IRagOrchestrator>(), logger.Object);

        var result = await sut.ExecuteAsync("search", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("Required parameter 'query' is missing or empty.");
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(ex => ex is ArgumentException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
