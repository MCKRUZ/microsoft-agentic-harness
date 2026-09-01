using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
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

    // ── top_k coercion (#575) ──

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteAsync_TopKNotPositive_FailsWithoutCallingTheOrchestrator(int topK)
    {
        var orchestrator = new Mock<IRagOrchestrator>();
        var sut = new DocumentSearchTool(orchestrator.Object, Mock.Of<ILogger<DocumentSearchTool>>());

        var result = await sut.ExecuteAsync(
            "search", new Dictionary<string, object?> { ["query"] = "q", ["top_k"] = topK });

        result.Success.Should().BeFalse("top_k must be a positive integer");
        orchestrator.Verify(
            o => o.SearchAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Domain.AI.RAG.Enums.RetrievalStrategy?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TopKOutOfIntRange_FailsRatherThanSilentlyWrapping()
    {
        // The regression this pins: GetOptionalInt used to do an unchecked (int)someLong cast. 2^32+100
        // is the adversarial case — its low 32 bits are exactly 100, an ordinary-looking positive
        // top_k that would sail straight past the min:1 floor and reach the orchestrator unnoticed. A
        // value that wraps to something non-positive would be refused by that floor regardless of
        // whether the cast itself is range-checked, so it would not actually exercise this fix.
        var orchestrator = new Mock<IRagOrchestrator>();
        var sut = new DocumentSearchTool(orchestrator.Object, Mock.Of<ILogger<DocumentSearchTool>>());

        var result = await sut.ExecuteAsync(
            "search", new Dictionary<string, object?> { ["query"] = "q", ["top_k"] = 4_294_967_396L });

        result.Success.Should().BeFalse();
        orchestrator.Verify(
            o => o.SearchAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Domain.AI.RAG.Enums.RetrievalStrategy?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ValidTopK_PassesThroughToTheOrchestrator()
    {
        var orchestrator = new Mock<IRagOrchestrator>();
        orchestrator
            .Setup(o => o.SearchAsync(
                "q", 5, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagAssembledContext { AssembledText = "result", TotalTokens = 1, WasTruncated = false });
        var sut = new DocumentSearchTool(orchestrator.Object, Mock.Of<ILogger<DocumentSearchTool>>());

        var result = await sut.ExecuteAsync(
            "search", new Dictionary<string, object?> { ["query"] = "q", ["top_k"] = 5 });

        result.Success.Should().BeTrue();
        orchestrator.Verify(
            o => o.SearchAsync("q", 5, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
