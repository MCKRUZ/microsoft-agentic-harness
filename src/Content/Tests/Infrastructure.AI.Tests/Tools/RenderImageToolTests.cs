using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="RenderImageTool"/> — the generative-UI tool that delegates image display to the
/// connected client via <see cref="IClientToolBridge"/>. Covers operation validation, the no-client and
/// URL-validation failures (missing / non-https), the serialized payload, and timeout/cancellation.
/// </summary>
public sealed class RenderImageToolTests
{
    private const string ValidUrl = "https://example.com/cat.png";

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Metadata_IsCorrect()
    {
        var sut = new RenderImageTool(new FakeBridge());
        sut.Name.Should().Be("render_image");
        sut.SupportedOperations.Should().BeEquivalentTo(["render"]);
        sut.Description.Should().Contain("url");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderImageTool(bridge).ExecuteAsync("explode", Args(("url", ValidUrl)));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoClientAttached_Fails()
    {
        var sut = new RenderImageTool(new FakeBridge { ClientAttached = false });
        var result = await sut.ExecuteAsync("render", Args(("url", ValidUrl)));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("client");
    }

    [Fact]
    public async Task ExecuteAsync_MissingUrl_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderImageTool(bridge).ExecuteAsync("render", Args(("alt", "a cat")));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Theory]
    [InlineData("http://example.com/cat.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("not a url")]
    public async Task ExecuteAsync_NonHttpsUrl_Fails(string url)
    {
        var bridge = new FakeBridge();
        var result = await new RenderImageTool(bridge).ExecuteAsync("render", Args(("url", url)));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("https");
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_PassesSerializedArgs_AndReturnsAck()
    {
        var bridge = new FakeBridge { Result = "Displayed the image to the user." };
        var result = await new RenderImageTool(bridge).ExecuteAsync(
            "render", Args(("url", ValidUrl), ("caption", "A cat")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Displayed the image to the user.");
        bridge.LastToolName.Should().Be("render_image");
        bridge.LastArgsJson.Should().Contain(ValidUrl).And.Contain("A cat");
    }

    [Fact]
    public async Task ExecuteAsync_BridgeTimeout_FailsGracefully()
    {
        var sut = new RenderImageTool(new FakeBridge { Throw = new TimeoutException() });
        var result = await sut.ExecuteAsync("render", Args(("url", ValidUrl)));
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_Propagates()
    {
        var sut = new RenderImageTool(new FakeBridge { Throw = new OperationCanceledException() });
        var act = async () => await sut.ExecuteAsync("render", Args(("url", ValidUrl)));
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
