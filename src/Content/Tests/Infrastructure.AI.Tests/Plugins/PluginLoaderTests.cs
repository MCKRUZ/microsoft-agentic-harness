using System.Text.Json;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly PluginLoader _sut;
    private readonly SkillsConfig _skillsConfig;
    private readonly McpServersConfig _mcpServersConfig;
    private readonly string _tempDir;

    public PluginLoaderTests()
    {
        _skillsConfig = new SkillsConfig();
        _mcpServersConfig = new McpServersConfig();
        _sut = new PluginLoader(
            _skillsConfig,
            _mcpServersConfig,
            NullLogger<PluginLoader>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"plugin-loader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private PluginDeclaration MakeDeclaration(string name = "test-plugin") =>
        new() { Name = name, Path = _tempDir, Enabled = true };

    [Fact]
    public void Load_WithSkillsDirectory_AddsToSkillsConfig()
    {
        var skillsDir = Path.Combine(_tempDir, "skills");
        Directory.CreateDirectory(skillsDir);

        var manifest = new PluginManifest
        {
            Name = "test-plugin",
            Version = "1.0.0",
            Skills = "./skills/"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration(), manifest);

        result.Should().NotBeNull();
        result!.Status.Should().Be(PluginLoadStatus.Loaded);
        result.SkillPaths.Should().ContainSingle().Which.Should().Be(skillsDir);
        _skillsConfig.AdditionalPaths.Should().Contain(skillsDir);
    }

    [Fact]
    public void Load_WithMcpJson_MergesNamespacedServers()
    {
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["azure"] = new { command = "npx", args = new[] { "azure-mcp" } }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, ".mcp.json"),
            JsonSerializer.Serialize(mcpConfig));

        var manifest = new PluginManifest
        {
            Name = "azure-plugin",
            Version = "1.0.0",
            McpServers = "./.mcp.json"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("azure-plugin"), manifest);

        result.Should().NotBeNull();
        result!.McpServerNames.Should().ContainSingle("azure-plugin:azure");
        _mcpServersConfig.Servers.Should().ContainKey("azure-plugin:azure");
        _mcpServersConfig.Servers["azure-plugin:azure"].Command.Should().Be("npx");
    }

    [Fact]
    public void Load_WithHttpMcpJson_MergesNamespacedHttpServerWithUrl()
    {
        // Root cause #1 of issue #368: PluginLoader hardcoded Stdio before McpServerDefinitionBuilder was
        // extracted, so an Http-type entry was silently mis-built. Proves the shared builder fixed it here too.
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["remote"] = new { type = "http", url = "https://tools.example.com/mcp" }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, ".mcp.json"),
            JsonSerializer.Serialize(mcpConfig));

        var manifest = new PluginManifest
        {
            Name = "remote-plugin",
            Version = "1.0.0",
            McpServers = "./.mcp.json"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("remote-plugin"), manifest);

        result.Should().NotBeNull();
        _mcpServersConfig.Servers["remote-plugin:remote"].Type.Should().Be(McpServerType.Http);
        _mcpServersConfig.Servers["remote-plugin:remote"].Url.Should().Be("https://tools.example.com/mcp");
    }

    [Fact]
    public void Load_EnvOverrides_MergedIntoMcpServers()
    {
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["server"] = new
                {
                    command = "node",
                    args = new[] { "server.js" },
                    env = new Dictionary<string, string> { ["KEY"] = "original" }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, ".mcp.json"),
            JsonSerializer.Serialize(mcpConfig));

        var declaration = MakeDeclaration();
        declaration.Env["KEY"] = "overridden";

        var manifest = new PluginManifest
        {
            Name = "test-plugin",
            Version = "1.0.0",
            McpServers = "./.mcp.json"
        };

        _sut.Load(_tempDir, declaration, manifest);

        _mcpServersConfig.Servers["test-plugin:server"].Env["KEY"].Should().Be("overridden");
    }

    [Fact]
    public void Load_NoSkillsOrMcp_ReturnsLoadedWithEmptyLists()
    {
        var manifest = new PluginManifest
        {
            Name = "bare",
            Version = "1.0.0"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("bare"), manifest);

        result.Should().NotBeNull();
        result!.Status.Should().Be(PluginLoadStatus.Loaded);
        result.SkillPaths.Should().BeEmpty();
        result.McpServerNames.Should().BeEmpty();
    }

    [Fact]
    public void Load_SkillsDirectoryMissing_SkipsSilently()
    {
        var manifest = new PluginManifest
        {
            Name = "no-skills",
            Version = "1.0.0",
            Skills = "./nonexistent-skills/"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("no-skills"), manifest);

        result.Should().NotBeNull();
        result!.SkillPaths.Should().BeEmpty();
    }

    [Fact]
    public void Load_McpJsonMissing_SkipsSilently()
    {
        var manifest = new PluginManifest
        {
            Name = "no-mcp",
            Version = "1.0.0",
            McpServers = "./missing.mcp.json"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("no-mcp"), manifest);

        result.Should().NotBeNull();
        result!.McpServerNames.Should().BeEmpty();
    }
}
