using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="RenderFormTool"/> — the generative-UI tool that displays an interactive form via
/// the client round-trip. Covers operation validation, the no-client failure, field-spec validation
/// (empty, missing name, bad type, select-without-options), the serialized payload, and the ack.
/// </summary>
public sealed class RenderFormToolTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static Dictionary<string, object?> FieldOf(string name, string type, object? options = null)
    {
        var f = new Dictionary<string, object?> { ["name"] = name, ["type"] = type };
        if (options is not null) f["options"] = options;
        return f;
    }

    private static object[] Fields(params Dictionary<string, object?>[] fields) => fields;

    [Fact]
    public void Metadata_IsCorrect()
    {
        var sut = new RenderFormTool(new FakeBridge());
        sut.Name.Should().Be("render_form");
        sut.SupportedOperations.Should().BeEquivalentTo(["render"]);
        sut.Description.Should().Contain("fields");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderFormTool(bridge).ExecuteAsync("explode", Args(("fields", Fields(FieldOf("email", "text")))));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoClientAttached_Fails()
    {
        var sut = new RenderFormTool(new FakeBridge { ClientAttached = false });
        var result = await sut.ExecuteAsync("render", Args(("fields", Fields(FieldOf("email", "text")))));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("client");
    }

    [Fact]
    public async Task ExecuteAsync_MissingFields_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(("title", "Sign up")));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFields_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(("fields", Array.Empty<object>())));
        result.Success.Should().BeFalse();
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_FieldMissingName_Fails()
    {
        var bridge = new FakeBridge();
        var badField = new Dictionary<string, object?> { ["type"] = "text" }; // no name
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(("fields", Fields(badField))));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("name");
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownFieldType_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(("fields", Fields(FieldOf("x", "hologram")))));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("type");
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_SelectWithoutOptions_Fails()
    {
        var bridge = new FakeBridge();
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(("fields", Fields(FieldOf("color", "select")))));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("options");
        bridge.InvokeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_PassesSerializedArgs_AndReturnsAck()
    {
        var bridge = new FakeBridge { Result = "Displayed the form to the user; their answers will arrive as their next message." };
        var result = await new RenderFormTool(bridge).ExecuteAsync("render", Args(
            ("title", "Preferences"),
            ("fields", Fields(FieldOf("email", "text"), FieldOf("color", "select", new[] { "red", "blue" })))));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Displayed the form");
        bridge.LastToolName.Should().Be("render_form");
        bridge.LastArgsJson.Should().Contain("email").And.Contain("select").And.Contain("blue");
    }

    [Fact]
    public async Task ExecuteAsync_BridgeTimeout_FailsGracefully()
    {
        var sut = new RenderFormTool(new FakeBridge { Throw = new TimeoutException() });
        var result = await sut.ExecuteAsync("render", Args(("fields", Fields(FieldOf("email", "text")))));
        result.Success.Should().BeFalse();
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
