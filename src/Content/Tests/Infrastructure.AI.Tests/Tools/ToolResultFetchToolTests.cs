using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
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
/// Unit-level tests for <see cref="ToolResultFetchTool"/>'s own logic — offset parsing and page-text
/// pass-through. Redaction is no longer this tool's concern (security review, #563 second revision):
/// it happens once, inside the store, before a result is ever written — see
/// <c>FileSystemToolResultStoreTests</c>'s <c>redactBeforeStoring</c> tests for that coverage.
/// End-to-end round-trip behavior against the real store and a real ambient scope is covered by
/// <c>Presentation.Common.Tests.Composition.ToolResultFetchToolCompositionTests</c>.
/// </summary>
public sealed class ToolResultFetchToolTests
{
    private readonly Mock<IToolResultStore> _resultStore = new();
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
            NullLogger<ToolResultFetchTool>.Instance);
    }

    private static Dictionary<string, object?> Params(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public async Task ExecuteAsync_FinalPage_ReturnsPageTextUnchanged()
    {
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage
            {
                Text = "plain output",
                NextOffset = 12,
                TotalChars = 12
            });

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("plain output");
    }

    [Fact]
    public async Task ExecuteAsync_PageWithMoreAvailable_TrailerFollowsTheText()
    {
        _resultStore
            .Setup(s => s.RetrievePageAsync("result-1", "scope-1", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolResultPage
            {
                Text = "page-prefix",
                NextOffset = 11,
                TotalChars = 100
            });

        var tool = BuildTool();
        var result = await tool.ExecuteAsync("fetch", Params(("resultId", "result-1")));

        result.Output.Should().StartWith("page-prefix");
        result.Output.Should().Contain("offset=11");
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
