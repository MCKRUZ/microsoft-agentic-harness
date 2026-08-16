using System.Text.Json;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// Tests for <see cref="McpServerDefinitionBuilder"/> — the shared, type-branching builder used by both
/// <see cref="PluginLoader"/> (host-installed plugins) and bundle staging (issue #368) so the two never
/// independently decide how an <c>mcpServers</c> JSON entry maps to an <see cref="McpServerDefinition"/>.
/// Returns <see cref="Domain.Common.Result{T}"/>, not exceptions (issue #374): every field this builder
/// reads comes from externally-authored, untrusted manifest JSON.
/// </summary>
public sealed class McpServerDefinitionBuilderTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_NoTypeField_DefaultsToStdioAndParsesCommandArgs()
    {
        var element = Parse("""{ "command": "npx", "args": ["-y", "server-name"] }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Plugin: azure]", "azure-mcp");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        var definition = result.Value!;
        definition.Type.Should().Be(McpServerType.Stdio);
        definition.Command.Should().Be("npx");
        definition.Args.Should().BeEquivalentTo(["-y", "server-name"]);
        definition.Description.Should().Be("[Plugin: azure] azure-mcp");
        definition.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Build_ExplicitStdioType_ParsesCommandArgs()
    {
        var element = Parse("""{ "type": "stdio", "command": "node", "args": ["server.js"] }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "custom");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Type.Should().Be(McpServerType.Stdio);
        result.Value.Command.Should().Be("node");
    }

    [Fact]
    public void Build_HttpType_ParsesUrlAndLeavesCommandEmpty()
    {
        var element = Parse("""{ "type": "http", "url": "https://tools.example.com/mcp" }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Type.Should().Be(McpServerType.Http);
        result.Value.Url.Should().Be("https://tools.example.com/mcp");
        result.Value.Command.Should().BeEmpty();
    }

    [Fact]
    public void Build_SseType_ParsesUrl()
    {
        var element = Parse("""{ "type": "sse", "url": "https://tools.example.com/sse" }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Type.Should().Be(McpServerType.Sse);
        result.Value.Url.Should().Be("https://tools.example.com/sse");
    }

    [Theory]
    [InlineData("""{ "type": "http" }""")]
    [InlineData("""{ "type": "http", "url": null }""")]
    [InlineData("""{ "type": "http", "url": "" }""")]
    [InlineData("""{ "type": "http", "url": "   " }""")]
    public void Build_HttpTypeWithNoUsableUrl_Fails(string json)
    {
        // Regression test: a remote server with no usable url used to register with
        // IsRemoteServer == true and Url == null, passing the bundle-path stdio-rejection
        // gate and squatting a name that only fails much later, at connect time.
        var element = Parse(json);

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*remote*declares a Http transport with no 'url'*");
    }

    [Fact]
    public void Build_SseTypeWithNoUrl_Fails()
    {
        var element = Parse("""{ "type": "sse" }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*remote*declares a Sse transport with no 'url'*");
    }

    [Fact]
    public void Build_EnvFromManifestAndDeclarationOverride_DeclarationWins()
    {
        var element = Parse("""{ "command": "npx", "env": { "A": "manifest", "B": "manifest-only" } }""");
        var declarationEnv = new Dictionary<string, string> { ["A"] = "declaration" };

        var result = McpServerDefinitionBuilder.Build(element, declarationEnv, "[Plugin: p]", "s");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Env["A"].Should().Be("declaration", "declaration env overrides take precedence over manifest env");
        result.Value.Env["B"].Should().Be("manifest-only");
    }

    // -- Malformed JSON shapes on untrusted input (#374) --------------------------------------------

    [Fact]
    public void Build_TypeIsNotAString_FailsWithoutThrowing()
    {
        var element = Parse("""{ "type": 5 }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'type' as Number*");
    }

    [Fact]
    public void Build_UrlIsNotAString_FailsWithoutThrowing()
    {
        var element = Parse("""{ "type": "http", "url": 5 }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'url' as Number*");
    }

    [Fact]
    public void Build_CommandIsNotAString_FailsWithoutThrowing()
    {
        var element = Parse("""{ "command": true }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'command' as True*");
    }

    [Fact]
    public void Build_ArgsIsNotAnArray_FailsWithoutThrowing()
    {
        var element = Parse("""{ "command": "npx", "args": "not-an-array" }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'args' as String*");
    }

    [Fact]
    public void Build_ArgsElementIsNotAString_FailsWithoutThrowing()
    {
        var element = Parse("""{ "command": "npx", "args": ["ok", 5] }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares an 'args' element as Number*");
    }

    [Fact]
    public void Build_EnvIsNotAnObject_FailsWithoutThrowing()
    {
        var element = Parse("""{ "command": "npx", "env": ["not-an-object"] }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'env' as Array*");
    }

    [Fact]
    public void Build_EnvValueIsNotAString_FailsWithoutThrowing()
    {
        var element = Parse("""{ "command": "npx", "env": { "A": 5 } }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*'s' declares 'env.A' as Number*");
    }

    [Fact]
    public void Build_ServerEntryIsNotAnObject_FailsWithoutThrowing()
    {
        // Regression test found by /code-review: the per-server JSON value itself (not just its
        // properties) can be any JSON kind — a bundle's mcp.json can declare "badserver": "not-an-object"
        // instead of an object. JsonElement.TryGetProperty throws InvalidOperationException when called
        // on a non-Object element, and every ReadOptionalString/ReadArgs/ReadEnv call passes serverElement
        // as that receiver — so without a top-level kind guard, this throws uncaught instead of returning
        // a Result failure, exactly the defect #374 exists to close.
        var element = Parse("\"not-an-object\"");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "badserver");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainMatch("*badserver*String*");
    }

    [Fact]
    public void Build_UnrecognizedTypeString_StillDefaultsToStdio()
    {
        // Not a malformed shape — a recognized STRING that isn't "http"/"sse" is the pre-existing,
        // callers-depend-on-it default-to-stdio behaviour (BundleStagingService.LogStdioRejected relies
        // on an absent OR unrecognized type both meaning stdio), not a failure.
        var element = Parse("""{ "type": "carrier-pigeon", "command": "npx" }""");

        var result = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "s");

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        result.Value!.Type.Should().Be(McpServerType.Stdio);
    }
}
