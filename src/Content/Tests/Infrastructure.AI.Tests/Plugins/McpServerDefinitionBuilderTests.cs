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
/// </summary>
public sealed class McpServerDefinitionBuilderTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_NoTypeField_DefaultsToStdioAndParsesCommandArgs()
    {
        var element = Parse("""{ "command": "npx", "args": ["-y", "server-name"] }""");

        var definition = McpServerDefinitionBuilder.Build(element, NoEnv, "[Plugin: azure]", "azure-mcp");

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

        var definition = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "custom");

        definition.Type.Should().Be(McpServerType.Stdio);
        definition.Command.Should().Be("node");
    }

    [Fact]
    public void Build_HttpType_ParsesUrlAndLeavesCommandEmpty()
    {
        var element = Parse("""{ "type": "http", "url": "https://tools.example.com/mcp" }""");

        var definition = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        definition.Type.Should().Be(McpServerType.Http);
        definition.Url.Should().Be("https://tools.example.com/mcp");
        definition.Command.Should().BeEmpty();
    }

    [Fact]
    public void Build_SseType_ParsesUrl()
    {
        var element = Parse("""{ "type": "sse", "url": "https://tools.example.com/sse" }""");

        var definition = McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        definition.Type.Should().Be(McpServerType.Sse);
        definition.Url.Should().Be("https://tools.example.com/sse");
    }

    [Theory]
    [InlineData("""{ "type": "http" }""")]
    [InlineData("""{ "type": "http", "url": null }""")]
    [InlineData("""{ "type": "http", "url": "" }""")]
    [InlineData("""{ "type": "http", "url": "   " }""")]
    public void Build_HttpTypeWithNoUsableUrl_Throws(string json)
    {
        // Regression test: a remote server with no usable url used to register with
        // IsRemoteServer == true and Url == null, passing the bundle-path stdio-rejection
        // gate and squatting a name that only fails much later, at connect time.
        var element = Parse(json);

        var act = () => McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_SseTypeWithNoUrl_Throws()
    {
        var element = Parse("""{ "type": "sse" }""");

        var act = () => McpServerDefinitionBuilder.Build(element, NoEnv, "[Bundle: b1]", "remote");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Build_EnvFromManifestAndDeclarationOverride_DeclarationWins()
    {
        var element = Parse("""{ "command": "npx", "env": { "A": "manifest", "B": "manifest-only" } }""");
        var declarationEnv = new Dictionary<string, string> { ["A"] = "declaration" };

        var definition = McpServerDefinitionBuilder.Build(element, declarationEnv, "[Plugin: p]", "s");

        definition.Env["A"].Should().Be("declaration", "declaration env overrides take precedence over manifest env");
        definition.Env["B"].Should().Be("manifest-only");
    }
}
