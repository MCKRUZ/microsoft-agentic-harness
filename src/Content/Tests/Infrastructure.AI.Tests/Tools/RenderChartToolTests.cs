using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="RenderChartTool"/> — the generative-UI tool that delegates chart rendering to
/// the connected client via <see cref="IClientToolBridge"/>. Covers operation validation, the no-client
/// and missing-metric failures, the serialized payload, and timeout/cancellation handling.
/// </summary>
public sealed class RenderChartToolTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var sut = new RenderChartTool(new FakeBridge());
        sut.Name.Should().Be("render_chart");
        sut.SupportedOperations.Should().BeEquivalentTo(["render"]);
        sut.Description.Should().Contain("metricId");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderChartTool(bridge).ExecuteAsync("explode", Args(("metricId", "x")));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoClientAttached_Fails()
    {
        var sut = new RenderChartTool(new FakeBridge { ClientAttached = false });
        var result = await sut.ExecuteAsync("render", Args(("metricId", "tokens_by_model")));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("client");
    }

    [Fact]
    public async Task ExecuteAsync_NoMetricOrQuery_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderChartTool(bridge).ExecuteAsync("render", Args(("chartType", "bar")));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_PassesSerializedArgs_AndReturnsSummary()
    {
        var bridge = new FakeBridge { Result = "Rendered pie chart of tokens by model." };
        var result = await new RenderChartTool(bridge).ExecuteAsync(
            "render", Args(("metricId", "tokens_by_model"), ("chartType", "pie")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Rendered pie chart of tokens by model.");
        bridge.LastToolName.Should().Be("render_chart");
        bridge.LastArgsJson.Should().Contain("tokens_by_model").And.Contain("pie");
    }

    [Fact]
    public async Task ExecuteAsync_BridgeTimeout_FailsGracefully()
    {
        var sut = new RenderChartTool(new FakeBridge { Throw = new TimeoutException() });
        var result = await sut.ExecuteAsync("render", Args(("promQL", "up")));
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_Propagates()
    {
        var sut = new RenderChartTool(new FakeBridge { Throw = new OperationCanceledException() });
        var act = async () => await sut.ExecuteAsync("render", Args(("promQL", "up")));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeBridge : IClientToolBridge
    {
        public bool ClientAttached { get; init; } = true;
        public string Result { get; init; } = "ok";
        public Exception? Throw { get; init; }

        public int InvokeCount { get; private set; }
        public string? LastToolName { get; private set; }
        public string? LastArgsJson { get; private set; }

        public bool IsClientAttached => ClientAttached;

        public Task<string> InvokeAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            InvokeCount++;
            LastToolName = toolName;
            LastArgsJson = argumentsJson;
            if (Throw is not null) return Task.FromException<string>(Throw);
            return Task.FromResult(Result);
        }
    }
}
