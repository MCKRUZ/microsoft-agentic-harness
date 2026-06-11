using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Compression;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Domain.AI.Compression.Enums;
using Domain.AI.Compression.Models;
using Domain.AI.Context;
using Domain.Common;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class ToolOutputCompressionBehaviorTests
{
    private readonly Mock<IToolOutputCompressor> _compressor = new();
    private readonly Mock<IToolResultStore> _resultStore = new();
    private readonly Mock<IAgentExecutionContext> _executionContext = new();
    private readonly Mock<ISecretRedactor> _secretRedactor = new();
    private readonly Mock<ILogger<ToolOutputCompressionBehavior<ToolTestRequest, Result<ToolTestResponse>>>> _logger = new();
    private readonly ToolOutputCompressionConfig _config = new()
    {
        Enabled = true,
        DefaultTokenThreshold = 2000
    };

    public ToolOutputCompressionBehaviorTests()
    {
        // Pass-through redactor so existing assertions observe unmodified tool output.
        _secretRedactor.Setup(r => r.Redact(It.IsAny<string?>())).Returns((string? s) => s);
    }

    private ToolOutputCompressionBehavior<ToolTestRequest, Result<ToolTestResponse>> CreateBehavior(
        ToolOutputCompressionConfig? config = null)
    {
        var options = Options.Create(config ?? _config);
        _executionContext.Setup(x => x.ConversationId).Returns("session-1");
        return new ToolOutputCompressionBehavior<ToolTestRequest, Result<ToolTestResponse>>(
            _compressor.Object,
            _resultStore.Object,
            _executionContext.Object,
            _secretRedactor.Object,
            options,
            _logger.Object);
    }

    [Fact]
    public async Task Handle_NonToolRequest_PassesThrough()
    {
        var behavior = new ToolOutputCompressionBehavior<NonToolRequest, string>(
            _compressor.Object,
            _resultStore.Object,
            _executionContext.Object,
            _secretRedactor.Object,
            Options.Create(_config),
            Mock.Of<ILogger<ToolOutputCompressionBehavior<NonToolRequest, string>>>());

        var result = await behavior.Handle(
            new NonToolRequest(),
            () => Task.FromResult("passthrough"),
            CancellationToken.None);

        Assert.Equal("passthrough", result);
        _compressor.Verify(
            x => x.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_BelowThreshold_PassesThrough()
    {
        var output = new ToolTestResponse("short output");
        var behavior = CreateBehavior();

        var result = await behavior.Handle(
            new ToolTestRequest("test_tool"),
            () => Task.FromResult(Result<ToolTestResponse>.Success(output)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("short output", result.Value!.ToolOutput);
        _compressor.Verify(
            x => x.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_AboveThreshold_CompressesAndStoresReference()
    {
        // Generate output that exceeds 2000 tokens (~8000 chars)
        var largeOutput = new string('x', 9000);
        var output = new ToolTestResponse(largeOutput);
        var behavior = CreateBehavior();

        var reference = new ToolResultReference
        {
            ResultId = "ref-123",
            ToolName = "test_tool",
            PreviewContent = "preview...",
            SizeChars = 9000,
            Timestamp = DateTimeOffset.UtcNow
        };

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reference);

        _compressor.Setup(x => x.CompressAsync(largeOutput, null, 2000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionResult
            {
                Output = "compressed summary",
                OriginalTokens = 2250,
                CompressedTokens = 5,
                Strategy = "Json",
                WasCompressed = true
            });

        var result = await behavior.Handle(
            new ToolTestRequest("test_tool"),
            () => Task.FromResult(Result<ToolTestResponse>.Success(output)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("compressed summary", result.Value!.ToolOutput);
        Assert.Contains("[Full output: result://ref-123]", result.Value!.ToolOutput);

        _resultStore.Verify(
            x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<CancellationToken>()),
            Times.Once);
        _compressor.Verify(
            x => x.CompressAsync(largeOutput, null, 2000, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DisabledConfig_PassesThrough()
    {
        var output = new ToolTestResponse(new string('x', 9000));
        var behavior = CreateBehavior(new ToolOutputCompressionConfig { Enabled = false });

        var result = await behavior.Handle(
            new ToolTestRequest("test_tool"),
            () => Task.FromResult(Result<ToolTestResponse>.Success(output)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(output.ToolOutput, result.Value!.ToolOutput);
        _compressor.Verify(
            x => x.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_CompressorThrows_ReturnsOriginalWithWarning()
    {
        var largeOutput = new string('x', 9000);
        var output = new ToolTestResponse(largeOutput);
        var behavior = CreateBehavior();

        var reference = new ToolResultReference
        {
            ResultId = "ref-456",
            ToolName = "test_tool",
            PreviewContent = "preview...",
            SizeChars = 9000,
            Timestamp = DateTimeOffset.UtcNow
        };

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reference);

        _compressor.Setup(x => x.CompressAsync(largeOutput, null, 2000, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Compressor exploded"));

        var result = await behavior.Handle(
            new ToolTestRequest("test_tool"),
            () => Task.FromResult(Result<ToolTestResponse>.Success(output)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(largeOutput, result.Value!.ToolOutput);
    }

    [Fact]
    public async Task Handle_NullToolOutput_PassesThrough()
    {
        var output = new ToolTestResponse(null!);
        var behavior = CreateBehavior();

        var result = await behavior.Handle(
            new ToolTestRequest("test_tool"),
            () => Task.FromResult(Result<ToolTestResponse>.Success(output)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.ToolOutput);
        _compressor.Verify(
            x => x.CompressAsync(It.IsAny<string>(), It.IsAny<ToolOutputCategory?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Test records (public required for Moq's Castle proxy on ILogger<T> generic args) ---

    public sealed record NonToolRequest : IRequest<string>;

    public sealed record ToolTestRequest(string ToolName) : IRequest<ToolTestResponse>, IToolRequest;

    public sealed record ToolTestResponse(string ToolOutput) : IToolResponse
    {
        public IToolResponse WithSanitizedOutput(string sanitizedOutput) =>
            new ToolTestResponse(sanitizedOutput);
    }
}
