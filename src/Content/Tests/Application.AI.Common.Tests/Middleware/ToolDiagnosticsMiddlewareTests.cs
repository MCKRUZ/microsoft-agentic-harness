using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Middleware;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Middleware;

public sealed class ToolDiagnosticsMiddlewareTests
{
    private static Mock<IChatClient> MakeChatClient(ChatResponse? response = null)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        mock.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);
        return mock;
    }

    private static (Mock<ITraceWriter> Writer, ToolDiagnosticsMiddleware Middleware)
        MakeMiddlewareWithWriter(Mock<IChatClient> innerClient)
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object);

        return (writerMock, middleware);
    }

    // --- Regression: trace appending when function results present ---

    [Fact]
    public async Task InvokeNext_WhenFunctionResultsInMessages_AppendsTraceRecord()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "file content")])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.Type == TraceRecordTypes.ToolResult),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeNext_AppendTraceThrows_DoesNotRethrow()
    {
        var innerClient = MakeChatClient();
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        // Should not propagate the IOException from AppendTraceAsync
        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InvokeNext_NoFunctionResultsInMessages_DoesNotCallAppendTrace()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "What is the weather?")
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeNext_WithoutTraceWriter_DoesNotThrow()
    {
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        var act = () => middleware.GetResponseAsync(messages, null, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
