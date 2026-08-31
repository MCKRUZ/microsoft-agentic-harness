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
        // #561: sourced from ToolResultScopeId, not ConversationId — the same scope tool_result_fetch
        // reads back with. Both setups kept so a test asserting on either property still gets a value.
        _executionContext.Setup(x => x.ConversationId).Returns("session-1");
        _executionContext.Setup(x => x.ToolResultScopeId).Returns("session-1");
        // #559: most tests using this factory ARE exercising the spill path and need the write to
        // actually happen; a test specifically proving the no-op-when-unretrievable guard overrides
        // this on its own mock instead of going through CreateBehavior.
        _executionContext.Setup(x => x.HasRetrievableToolResultScope).Returns(true);
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
            FullContentPath = "/fake/persisted.json",
            SizeChars = 9000,
            Timestamp = DateTimeOffset.UtcNow
        };

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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
        // #561: the same marker shape ToolCallAdmissionPipeline emits and ToolResultFetchTool
        // recognizes — this behavior used to hand-roll its own "result://" phrase that nothing resolved.
        Assert.Contains(
            string.Format(
                Application.AI.Common.Services.Governance.ToolCallAdmissionPipeline.SpilledResultMarkerFormat,
                "ref-123"),
            result.Value!.ToolOutput);

        _resultStore.Verify(
            x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _compressor.Verify(
            x => x.CompressAsync(largeOutput, null, 2000, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompressIfNeeded_SpilledResult_EmitsTheSameMarkerShapeToolResultFetchAdvertises()
    {
        // #561: pins the marker shape directly against the pipeline's own constant, so this behavior
        // and ToolResultFetchTool can never silently drift apart the way they already had once.
        var largeOutput = new string('x', 9000);
        var output = new ToolTestResponse(largeOutput);
        var behavior = CreateBehavior();

        var reference = new ToolResultReference
        {
            ResultId = "ref-789",
            ToolName = "test_tool",
            PreviewContent = "preview...",
            FullContentPath = "/fake/persisted.json",
            SizeChars = 9000,
            Timestamp = DateTimeOffset.UtcNow
        };

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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

        Assert.Contains(
            string.Format(
                Application.AI.Common.Services.Governance.ToolCallAdmissionPipeline.SpilledResultMarkerFormat,
                "ref-789"),
            result.Value!.ToolOutput);
    }

    [Fact]
    public async Task CompressIfNeeded_StoreKeptContentInline_DoesNotAdvertiseAnUnfetchableMarker()
    {
        // Correctness-review finding: this behavior's own threshold is token-based (~2000 tokens,
        // ~8000 chars) while StoreIfLargeAsync's spill decision is char-based (PerResultCharLimit,
        // 50,000 by default) — an output can trip compression without exceeding the store's own
        // threshold, in which case StoreIfLargeAsync keeps it inline (FullContentPath null, no file
        // written). Appending the retrieval marker unconditionally, as an earlier version of this fix
        // did, advertised an id tool_result_fetch could never resolve for every output in that band.
        var largeOutput = new string('x', 9000);
        var output = new ToolTestResponse(largeOutput);
        var behavior = CreateBehavior();

        var reference = new ToolResultReference
        {
            ResultId = "ref-inline",
            ToolName = "test_tool",
            PreviewContent = largeOutput,
            FullContentPath = null,
            SizeChars = 9000,
            Timestamp = DateTimeOffset.UtcNow
        };

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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

        Assert.Equal("compressed summary", result.Value!.ToolOutput);
        Assert.DoesNotContain("tool_result_fetch", result.Value!.ToolOutput);
    }

    [Fact]
    public async Task CompressIfNeeded_WithNoRetrievableScope_SkipsTheStoreWriteEntirely()
    {
        // #559: mirrors ToolCallAdmissionPipeline.SpillAndBuildMarkerAsync's identical guard — a
        // direct tool invocation mints a fresh, call-scoped ToolResultScopeId that dies with the
        // call, so a file written here would be unreachable the instant this method returns.
        var largeOutput = new string('x', 9000);
        var output = new ToolTestResponse(largeOutput);
        var behavior = CreateBehavior();
        // Must override AFTER CreateBehavior — it sets this mock to true, and Moq honors the LAST
        // matching setup.
        _executionContext.Setup(x => x.HasRetrievableToolResultScope).Returns(false);

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

        Assert.Equal("compressed summary", result.Value!.ToolOutput);
        _resultStore.Verify(
            x => x.StoreIfLargeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never,
            "the write itself must be skipped, not just the retrieval id");
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

        _resultStore.Setup(x => x.StoreIfLargeAsync("session-1", "test_tool", null, largeOutput, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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
