using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Middleware;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Middleware;

/// <summary>
/// Tests for <see cref="ToolDiagnosticsMiddleware"/> covering trace appending for
/// function results, error resilience, tool deduplication, tool logging,
/// tool call response logging, response preview, streaming path, and null logger guard.
/// </summary>
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

    private static Mock<IChatClient> MakeStreamingChatClient(params ChatResponseUpdate[] chunks)
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());
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

    // --- Constructor validation ---

    [Fact]
    public void Ctor_NullLogger_ThrowsArgumentNull()
    {
        var innerClient = new Mock<IChatClient>().Object;

        var act = () => new ToolDiagnosticsMiddleware(innerClient, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // --- Trace appending ---

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

    // --- Tool deduplication ---

    [Fact]
    public async Task GetResponseAsync_DuplicateToolNames_DeduplicatesBeforeSendingToInner()
    {
        ChatOptions? capturedOptions = null;
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool1 = AIFunctionFactory.Create(() => "a", "my_tool");
        var tool2 = AIFunctionFactory.Create(() => "b", "my_tool");
        var tool3 = AIFunctionFactory.Create(() => "c", "other_tool");
        var options = new ChatOptions { Tools = [tool1, tool2, tool3] };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(2);
        capturedOptions.Tools.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetResponseAsync_NoTools_DoesNotDeduplicateOrFail()
    {
        var innerClient = MakeChatClient();
        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var act = () => middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetResponseAsync_SingleTool_NoDeduplicationNeeded()
    {
        ChatOptions? capturedOptions = null;
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool = AIFunctionFactory.Create(() => "a", "my_tool");
        var options = new ChatOptions { Tools = [tool] };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(1);
    }

    // --- Tool call logging in response ---

    [Fact]
    public async Task GetResponseAsync_ResponseWithToolCalls_LogsToolCallInfo()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "search_tool")
            ])
        ]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "search_tool")]
        };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "search")], options);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("search_tool")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetResponseAsync_NoToolCallsButToolsConfigured_LogsWarning()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "I'll just respond with text.")]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "ok", "available_tool")]
        };

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("No tool calls")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetResponseAsync_NoToolCallsNoToolsConfigured_LogsDebug()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Just text")]);
        var innerClient = MakeChatClient(response);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null);

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("generation-only")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // --- Redactor integration ---

    [Fact]
    public async Task InvokeNext_WithRedactor_RedactsPayloadBeforeTracing()
    {
        var innerClient = MakeChatClient();
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writerMock = new Mock<ITraceWriter>();
        writerMock.Setup(w => w.Scope).Returns(scope);
        writerMock
            .Setup(w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance,
            traceWriter: writerMock.Object,
            redactor: redactor.Object);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "secret data")])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.PayloadSummary == "[REDACTED]"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Multiple function results ---

    [Fact]
    public async Task InvokeNext_MultipleFunctionResults_AppendsTraceForEach()
    {
        var innerClient = MakeChatClient();
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", result: "result-1"),
                new FunctionResultContent("call-2", result: "result-2")
            ])
        };

        await middleware.GetResponseAsync(messages, null, CancellationToken.None);

        writerMock.Verify(
            w => w.AppendTraceAsync(It.IsAny<ExecutionTraceRecord>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // --- Streaming path ---

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsAllChunks()
    {
        var chunk1 = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Hello")] };
        var chunk2 = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(" world")] };
        var innerClient = MakeStreamingChatClient(chunk1, chunk2);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var received = new List<ChatResponseUpdate>();
        await foreach (var chunk in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            received.Add(chunk);
        }

        received.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_DeduplicatesTools()
    {
        ChatOptions? capturedOptions = null;
        var chunks = new[] { new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] } };
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .Returns(chunks.ToAsyncEnumerable());
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var tool1 = AIFunctionFactory.Create(() => "a", "search");
        var tool2 = AIFunctionFactory.Create(() => "b", "search");
        var options = new ChatOptions { Tools = [tool1, tool2] };

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // consume
        }

        capturedOptions.Should().NotBeNull();
        capturedOptions!.Tools.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithTraceWriter_AppendsFunctionResultTraces()
    {
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);
        var (writerMock, middleware) = MakeMiddlewareWithWriter(innerClient);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "tool output")])
        };

        await foreach (var _ in middleware.GetStreamingResponseAsync(messages))
        {
            // consume
        }

        writerMock.Verify(
            w => w.AppendTraceAsync(
                It.Is<ExecutionTraceRecord>(r => r.Type == TraceRecordTypes.ToolResult),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutTraceWriter_DoesNotThrow()
    {
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            NullLogger<ToolDiagnosticsMiddleware>.Instance);

        var messages = new ChatMessage[]
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-1", result: "content")])
        };

        var act = async () =>
        {
            await foreach (var _ in middleware.GetStreamingResponseAsync(messages))
            {
                // consume
            }
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LogsToolsInOptions()
    {
        var logger = new Mock<ILogger<ToolDiagnosticsMiddleware>>();
        var chunk = new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
        var innerClient = MakeStreamingChatClient(chunk);

        var middleware = new ToolDiagnosticsMiddleware(
            innerClient.Object,
            logger.Object);

        var tool = AIFunctionFactory.Create(() => "a", "my_streaming_tool");
        var options = new ChatOptions { Tools = [tool] };

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // consume
        }

        // Should log that tools were configured for streaming
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) =>
                    o.ToString()!.Contains("GetStreamingResponseAsync") &&
                    o.ToString()!.Contains("1")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
