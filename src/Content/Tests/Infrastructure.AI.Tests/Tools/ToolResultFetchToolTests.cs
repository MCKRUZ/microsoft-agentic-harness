using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Context;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Unit-level tests for <see cref="ToolResultFetchTool"/>'s own logic — offset parsing and the
/// explicit read-time redaction fix (security review, #563). End-to-end round-trip behavior against
/// the real store and a real ambient scope is covered by
/// <c>Presentation.Common.Tests.Composition.ToolResultFetchToolCompositionTests</c>.
/// </summary>
public sealed class ToolResultFetchToolTests
{
    private readonly Mock<IToolResultStore> _resultStore = new();
    private readonly Mock<IContentRedactionFilter> _redactionFilter = new();
    private readonly AppConfig _config = new();

    private ToolResultFetchTool BuildTool(string toolResultScopeId = "scope-1")
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IAgentExecutionContext>(c => c.ToolResultScopeId == toolResultScopeId));
        var scope = services.BuildServiceProvider();

        var options = new Mock<IOptionsMonitor<AppConfig>>();
        options.Setup(o => o.CurrentValue).Returns(_config);

        return new ToolResultFetchTool(
            _resultStore.Object,
            Mock.Of<IAmbientRequestScope>(a => a.Current == (IServiceProvider)scope),
            options.Object,
            _redactionFilter.Object,
            NullLogger<ToolResultFetchTool>.Instance);
    }

    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public async Task ExecuteAsync_PageRequiresRedaction_RedactsBeforeReturning()
    {
        // #563 security-review finding: the pipeline's own read-time pass resolves its redaction
        // decision from tool_result_fetch's own classification, not from the classification the
        // ORIGINATING call ran under — RedactOnRetrieve is what carries that original verdict, and
        // this tool must act on it directly rather than trusting the pipeline to reach it a second way.
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage
            {
                Text = "secret-api-key-12345",
                NextOffset = 20,
                TotalChars = 20,
                RedactOnRetrieve = true
            });
        _redactionFilter
            .Setup(f => f.Redact("secret-api-key-12345", RedactionCategories.All))
            .Returns("[REDACTED]");

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("[REDACTED]");
        _redactionFilter.Verify(f => f.Redact("secret-api-key-12345", RedactionCategories.All), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PageDoesNotRequireRedaction_ReturnsTextUnchanged()
    {
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage
            {
                Text = "plain output",
                NextOffset = 12,
                TotalChars = 12,
                RedactOnRetrieve = false
            });

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Output.Should().Be("plain output");
        _redactionFilter.Verify(
            f => f.Redact(It.IsAny<string>(), It.IsAny<IReadOnlyList<RedactionCategory>>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RedactedPageWithMoreAvailable_TrailerFollowsRedactedText()
    {
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage
            {
                Text = "secret-prefix",
                NextOffset = 13,
                TotalChars = 100,
                RedactOnRetrieve = true
            });
        _redactionFilter
            .Setup(f => f.Redact("secret-prefix", RedactionCategories.All))
            .Returns("[REDACTED]");

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Output.Should().StartWith("[REDACTED]");
        result.Output.Should().Contain("offset=13");
        result.Output.Should().NotContain("secret-prefix", "the raw, unredacted text must never reach the return value");
    }

    [Fact]
    public async Task ExecuteAsync_MissingResultId_FailsWithoutCallingTheStore()
    {
        var tool = BuildTool();

        var result = await tool.ExecuteAsync("fetch", Params());

        result.Success.Should().BeFalse();
        _resultStore.Verify(
            s => s.RetrievePageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData(-1)]
    public async Task ExecuteAsync_MalformedOffset_FailsCleanly(object offset)
    {
        var tool = BuildTool();

        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1"), ("offset", offset)));

        result.Success.Should().BeFalse();
        _resultStore.Verify(
            s => s.RetrievePageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OffsetSuppliedAsLong_IsAccepted()
    {
        // MCP/JSON tool arguments commonly box integral numbers as long, not int.
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 42, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage { Text = "page", NextOffset = 46, TotalChars = 46 });

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1"), ("offset", 42L)));

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoAmbientScope_FailsWithoutCallingTheStore()
    {
        var tool = new ToolResultFetchTool(
            _resultStore.Object,
            Mock.Of<IAmbientRequestScope>(a => a.Current == null),
            Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == _config),
            _redactionFilter.Object,
            NullLogger<ToolResultFetchTool>.Instance);

        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Success.Should().BeFalse();
        _resultStore.Verify(
            s => s.RetrievePageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ResultIdNotFound_FailsWithGenericMessage()
    {
        _resultStore
            .Setup(s => s.RetrievePageAsync("missing", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "missing")));

        result.Success.Should().BeFalse();
    }
}
