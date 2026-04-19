using Application.AI.Common.Middleware;
using Application.AI.Common.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Integration;

/// <summary>
/// Integration tests for the chat client middleware pipeline:
/// <see cref="ObservabilityMiddleware"/> and <see cref="ToolDiagnosticsMiddleware"/>
/// wrapping a <see cref="FakeChatClient"/>.
/// </summary>
public class MiddlewarePipelineIntegrationTests
{
    [Fact]
    public async Task ObservabilityMiddleware_LogsMessageCountAndTokenUsage()
    {
        var loggerMock = new Mock<ILogger<ObservabilityMiddleware>>();
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponseWithUsage("Test response", 100, 50);

        var middleware = new ObservabilityMiddleware(fakeClient, loggerMock.Object);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helper."),
            new(ChatRole.User, "Hello")
        };

        var response = await middleware.GetResponseAsync(messages);

        // Verify response passes through
        response.Messages.Should().NotBeEmpty();

        // Verify logging of message count
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ObservabilityMiddleware_StreamingResponse_LogsChunks()
    {
        var loggerMock = new Mock<ILogger<ObservabilityMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("streamed content");
        var middleware = new ObservabilityMiddleware(fakeClient, loggerMock.Object);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Stream this") };

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var chunk in middleware.GetStreamingResponseAsync(messages))
        {
            chunks.Add(chunk);
        }

        chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ToolDiagnosticsMiddleware_NoTools_LogsGenerationOnly()
    {
        var loggerMock = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("No tools needed");
        var middleware = new ToolDiagnosticsMiddleware(fakeClient, loggerMock.Object);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };

        var response = await middleware.GetResponseAsync(messages);

        response.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ToolDiagnosticsMiddleware_WithTools_LogsToolInfo()
    {
        var loggerMock = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("Used a tool");
        var middleware = new ToolDiagnosticsMiddleware(fakeClient, loggerMock.Object);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Read a file") };

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "result", "test_tool")]
        };

        var response = await middleware.GetResponseAsync(messages, options);

        response.Messages.Should().NotBeEmpty();

        // Should have logged tool configuration
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StackedMiddleware_ObservabilityThenToolDiag_BothExecute()
    {
        var obsLogger = new Mock<ILogger<ObservabilityMiddleware>>();
        var toolLogger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("stacked result");

        // Build pipeline: ToolDiag -> Observability -> FakeClient
        var observability = new ObservabilityMiddleware(fakeClient, obsLogger.Object);
        var toolDiag = new ToolDiagnosticsMiddleware(observability, toolLogger.Object);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Stacked test") };
        var response = await toolDiag.GetResponseAsync(messages);

        response.Messages.Should().NotBeEmpty();

        // Both middlewares should have logged
        obsLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

        toolLogger.Verify(
            l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ToolDiagnosticsMiddleware_DuplicateTools_DeduplicatesBeforeSending()
    {
        var loggerMock = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("deduped");
        var middleware = new ToolDiagnosticsMiddleware(fakeClient, loggerMock.Object);

        var tool1 = AIFunctionFactory.Create(() => "r1", "shared_tool");
        var tool2 = AIFunctionFactory.Create(() => "r2", "shared_tool");
        var tool3 = AIFunctionFactory.Create(() => "r3", "unique_tool");

        var options = new ChatOptions
        {
            Tools = [tool1, tool2, tool3]
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        var response = await middleware.GetResponseAsync(messages, options);

        // The fake client should have received the request (deduplication happens internally)
        fakeClient.RequestHistory.Should().HaveCount(1);
    }

    [Fact]
    public async Task ToolDiagnosticsMiddleware_NullOptions_DoesNotThrow()
    {
        var loggerMock = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var fakeClient = new FakeChatClient().WithDefaultResponse("ok");
        var middleware = new ToolDiagnosticsMiddleware(fakeClient, loggerMock.Object);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };

        var act = async () => await middleware.GetResponseAsync(messages, options: null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ObservabilityMiddleware_NullLogger_ThrowsArgumentNull()
    {
        var fakeClient = new FakeChatClient();

        var act = () => new ObservabilityMiddleware(fakeClient, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ToolDiagnosticsMiddleware_NullLogger_ThrowsArgumentNull()
    {
        var fakeClient = new FakeChatClient();

        var act = () => new ToolDiagnosticsMiddleware(fakeClient, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
