using Application.AI.Common.Interfaces;
using Application.AI.Common.Middleware;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Middleware;

/// <summary>
/// Tests for <see cref="ObservabilityMiddleware"/> covering logging of message counts,
/// token usage, streaming chunk diagnostics, null logger guard, and no-usage path.
/// </summary>
public class ObservabilityMiddlewareTests
{
    private static ChatResponseUpdate MakeChunk(string text)
    {
        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)]
        };
    }

    [Fact]
    public void Ctor_NullLogger_ThrowsArgumentNull()
    {
        var innerClient = new Mock<IChatClient>().Object;

        var act = () => new ObservabilityMiddleware(innerClient, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetResponseAsync_LogsMessageCount()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        var innerClient = new Mock<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "hello")]);
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.System, "instructions")
        };

        var result = await middleware.GetResponseAsync(messages);

        result.Should().BeSameAs(response);
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("2")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetResponseAsync_WithUsage_LogsTokenCounts()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        var innerClient = new Mock<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                TotalTokenCount = 150
            }
        };
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);
        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("100") && o.ToString()!.Contains("50")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetResponseAsync_WithoutUsage_DoesNotLogTokenCounts()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        var innerClient = new Mock<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")]);
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);
        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("Token usage")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task GetResponseAsync_MessagesAsIEnumerable_CountsCorrectly()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        var innerClient = new Mock<IChatClient>();
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);

        // Pass as IEnumerable (not ICollection) -- middleware should materialize to count
        IEnumerable<ChatMessage> messages = Yield3Messages();

        await middleware.GetResponseAsync(messages);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("3")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LogsMessageCount()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        var innerClient = new Mock<IChatClient>();

        var chunks = new List<ChatResponseUpdate>
        {
            MakeChunk("Hello"),
            MakeChunk(" world")
        };

        innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.System, "be helpful")
        };

        var received = new List<ChatResponseUpdate>();
        await foreach (var chunk in middleware.GetStreamingResponseAsync(messages))
        {
            received.Add(chunk);
        }

        received.Should().HaveCount(2);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("streaming") && o.ToString()!.Contains("2")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LogsChunkDebugInfo()
    {
        var logger = new Mock<ILogger<ObservabilityMiddleware>>();
        logger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);

        var innerClient = new Mock<IChatClient>();
        var chunks = new List<ChatResponseUpdate>
        {
            MakeChunk("chunk1")
        };

        innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(chunks.ToAsyncEnumerable());
        innerClient.Setup(c => c.GetService(It.IsAny<Type>())).Returns(null!);

        var middleware = new ObservabilityMiddleware(innerClient.Object, logger.Object);

        await foreach (var _ in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            // consume
        }

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("chunk")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // Issue #592: Anthropic.SDK's IChatClient bridge builds its AdditionalCounts keys from
    // nameof(Usage.CacheReadInputTokens)/nameof(Usage.CacheCreationInputTokens) — the literal C#
    // property names, PascalCase — not the API's own snake_case wire field names. Reading only the
    // snake_case spelling meant every Claude call (via Foundry or direct) silently reported zero
    // cache savings. These two tests pin both spellings are read correctly, so a regression to a
    // single-key lookup fails loudly instead of silently zeroing Anthropic's cache telemetry again.

    [Fact]
    public async Task GetResponseAsync_AnthropicShapedPascalCaseCacheKeys_RecordsNonZeroCacheCounts()
    {
        var innerClient = new Mock<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["CacheReadInputTokens"] = 42,
                    ["CacheCreationInputTokens"] = 7,
                }
            }
        };
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var usageCapture = new Mock<ILlmUsageCapture>();
        var middleware = new ObservabilityMiddleware(
            innerClient.Object, new Mock<ILogger<ObservabilityMiddleware>>().Object, usageCapture.Object);

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        usageCapture.Verify(c => c.Record(100, 50, 42, 7, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task GetResponseAsync_SnakeCaseCacheKeys_StillRecordsNonZeroCacheCounts()
    {
        var innerClient = new Mock<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_read_input_tokens"] = 42,
                    ["cache_creation_input_tokens"] = 7,
                }
            }
        };
        innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var usageCapture = new Mock<ILlmUsageCapture>();
        var middleware = new ObservabilityMiddleware(
            innerClient.Object, new Mock<ILogger<ObservabilityMiddleware>>().Object, usageCapture.Object);

        await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        usageCapture.Verify(c => c.Record(100, 50, 42, 7, It.IsAny<string?>()), Times.Once);
    }

    private static IEnumerable<ChatMessage> Yield3Messages()
    {
        yield return new ChatMessage(ChatRole.System, "sys");
        yield return new ChatMessage(ChatRole.User, "msg1");
        yield return new ChatMessage(ChatRole.User, "msg2");
    }
}
