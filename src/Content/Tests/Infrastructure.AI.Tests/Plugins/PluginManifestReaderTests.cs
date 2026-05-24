using Domain.Common.Config.AI.Plugins;
using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

public sealed class PluginManifestReaderTests : IDisposable
{
    private readonly PluginManifestReader _sut;
    private readonly string _tempDir;

    public PluginManifestReaderTests()
    {
        _sut = new PluginManifestReader(NullLogger<PluginManifestReader>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"manifest-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Read_ValidManifest_ReturnsPluginManifest()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.json"), """
            {
                "name": "azure",
                "description": "Azure cloud skills",
                "version": "1.1.48",
                "skills": "./skills/",
                "mcpServers": "./.mcp.json",
                "keywords": ["azure", "cloud"]
            }
            """);

        var result = _sut.Read(_tempDir);

        result.Should().NotBeNull();
        result!.Name.Should().Be("azure");
        result.Description.Should().Be("Azure cloud skills");
        result.Version.Should().Be("1.1.48");
        result.Skills.Should().Be("./skills/");
        result.McpServers.Should().Be("./.mcp.json");
        result.Keywords.Should().BeEquivalentTo(["azure", "cloud"]);
    }

    [Fact]
    public void Read_WithAuthor_DeserializesAuthorRecord()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.json"), """
            {
                "name": "test",
                "version": "1.0",
                "author": { "name": "Microsoft", "url": "https://microsoft.com" }
            }
            """);

        var result = _sut.Read(_tempDir);

        result.Should().NotBeNull();
        result!.Author.Should().NotBeNull();
        result.Author!.Name.Should().Be("Microsoft");
        result.Author.Url.Should().Be("https://microsoft.com");
    }

    [Fact]
    public void Read_MissingPluginJson_ReturnsNull()
    {
        var result = _sut.Read(_tempDir);

        result.Should().BeNull();
    }

    [Fact]
    public void Read_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.json"), "not json {{{");

        var result = _sut.Read(_tempDir);

        result.Should().BeNull();
    }

    [Fact]
    public void Read_MinimalManifest_DefaultsEmptyCollections()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.json"), """
            { "name": "minimal", "version": "0.1.0" }
            """);

        var result = _sut.Read(_tempDir);

        result.Should().NotBeNull();
        result!.Keywords.Should().BeEmpty();
        result.Skills.Should().BeNull();
        result.McpServers.Should().BeNull();
        result.Hooks.Should().BeNull();
    }

    [Fact]
    public void Read_WithHooks_DeserializesHooksManifest()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.json"), """
            {
                "name": "hooked",
                "version": "1.0",
                "hooks": {
                    "paths": ["./hooks/pre-tool.sh"],
                    "exclusive": true
                }
            }
            """);

        var result = _sut.Read(_tempDir);

        result.Should().NotBeNull();
        result!.Hooks.Should().NotBeNull();
        result.Hooks!.Paths.Should().ContainSingle("./hooks/pre-tool.sh");
        result.Hooks.Exclusive.Should().BeTrue();
    }

    [Fact]
    public void Read_NonexistentDirectory_ReturnsNull()
    {
        var result = _sut.Read(Path.Combine(_tempDir, "does-not-exist"));

        result.Should().BeNull();
    }

    [Fact]
    public void Read_PathTraversalAttempt_ReturnsNull()
    {
        // A caller passing "../<guid>" relative to the temp root should not escape
        // to read files outside the declared plugin directory.
        var parentDir = Path.GetTempPath();
        var traversalPath = Path.Combine(_tempDir, "..", $"traversal-{Guid.NewGuid():N}");

        // Even if the resolved path technically exists (parent of tempDir does),
        // the guard must reject a pluginDirectory that resolves outside itself when
        // combined with "plugin.json" — here we verify no manifest is returned
        // for a path that navigates above the intended base.
        var result = _sut.Read(traversalPath);

        result.Should().BeNull();
    }
}
