using System.Text.Json;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging;
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
    public void Load_OneMalformedMcpServerEntry_SkipsItButLoadsTheRest()
    {
        // Regression test: a per-entry build failure used to propagate out of LoadMcpServers
        // uncaught, through Load's outer catch, marking the WHOLE plugin Failed with empty
        // skill/server lists -- discarding a skill path already collected in the same call and
        // orphaning any server registered by an earlier, good entry in the same mcpServers block.
        var skillsDir = Path.Combine(_tempDir, "skills");
        Directory.CreateDirectory(skillsDir);

        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["good"] = new { command = "npx", args = new[] { "good-mcp" } },
                ["bad"] = new { type = "http" } // no url -> McpServerDefinitionBuilder.Build fails (#374: Result<T>, not a throw)
            }
        };
        File.WriteAllText(
            Path.Combine(_tempDir, ".mcp.json"),
            JsonSerializer.Serialize(mcpConfig));

        var manifest = new PluginManifest
        {
            Name = "mixed-plugin",
            Version = "1.0.0",
            Skills = "./skills/",
            McpServers = "./.mcp.json"
        };

        var result = _sut.Load(_tempDir, MakeDeclaration("mixed-plugin"), manifest);

        result.Should().NotBeNull();
        result!.Status.Should().Be(PluginLoadStatus.Loaded,
            "one malformed MCP server entry must not fail the whole plugin");
        result.SkillPaths.Should().ContainSingle(skillsDir,
            "skills already collected before the malformed entry must not be discarded");
        result.McpServerNames.Should().ContainSingle("mixed-plugin:good");
        _mcpServersConfig.Servers.Should().ContainKey("mixed-plugin:good");
        _mcpServersConfig.Servers.Should().NotContainKey("mixed-plugin:bad");
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

    [Fact]
    public void Load_SomethingThrowsAfterSkillsWouldHaveCommitted_LeavesSkillsConfigUntouched()
    {
        // #614: the old shape committed the skill path to _skillsConfig.AdditionalPaths immediately
        // inside LoadSkills, then kept going — if ANYTHING later in the same Load call threw, the
        // outer catch marked the plugin Failed, but the already-committed skill path stayed in
        // AdditionalPaths. SkillMetadataRegistry.ResolvePluginSkillPaths only attributes a skill to a
        // plugin whose Status is Loaded, so that orphaned path's skill would resolve PluginSource =
        // null (indistinguishable from a built-in skill) and run with NO AllowedTools/DeniedTools
        // restriction at all — for exactly the plugin that failed to finish loading. Traced during
        // #614's investigation: no crafted manifest reproduces a live throw from the MCP-loading step
        // today (it's fully Result<T>-based), so this test injects a fault the honest way that IS
        // still reachable — a logging call failing, e.g. a broken log sink — to prove the commit is
        // atomic regardless of WHERE in the load a failure comes from, not just the specific trigger
        // that was in scope originally.
        var skillsDir = Path.Combine(_tempDir, "skills");
        Directory.CreateDirectory(skillsDir);

        var manifest = new PluginManifest
        {
            Name = "throws-late",
            Version = "1.0.0",
            Skills = "./skills/"
        };

        var sut = new PluginLoader(_skillsConfig, _mcpServersConfig, new ThrowingLogger());

        var result = sut.Load(_tempDir, MakeDeclaration("throws-late"), manifest);

        result.Should().NotBeNull();
        result!.Status.Should().Be(PluginLoadStatus.Failed,
            "the plugin never finished loading, so it must not report as Loaded");
        _skillsConfig.AdditionalPaths.Should().BeEmpty(
            "a skill path must never survive in shared config for a plugin that ended up Failed");
    }

    /// <summary>
    /// Throws on an Information-level log call specifically — real loggers can fail (a broken sink, a
    /// full disk, a network-based provider timing out), and #614's fix must hold regardless of which
    /// step in <see cref="PluginLoader.Load"/> a failure originates from, not just a manifest-content
    /// one. Deliberately spares Warning: <see cref="PluginLoader.Load"/>'s own catch block logs the
    /// failure at Warning, and that call must survive for the method to return a value at all rather
    /// than let a second, unrelated exception escape unhandled out of the test itself.
    /// </summary>
    private sealed class ThrowingLogger : ILogger<PluginLoader>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                throw new InvalidOperationException("Simulated logging sink failure.");
        }
    }
}
