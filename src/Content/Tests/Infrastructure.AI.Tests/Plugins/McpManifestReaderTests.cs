using FluentAssertions;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Plugins;

/// <summary>
/// Tests for <see cref="McpManifestReader"/> — the shared locate/guard/parse helper used by both
/// <see cref="PluginLoader"/> (host-installed plugins) and bundle staging, so a malformed manifest
/// degrades identically (skip, log, never throw past this boundary) everywhere it's read.
/// </summary>
public sealed class McpManifestReaderTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"mcp-manifest-reader-tests-{Guid.NewGuid():N}");

    public McpManifestReaderTests() => Directory.CreateDirectory(_baseDir);

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Fact]
    public void ReadMcpServersBlock_ValidObject_ReturnsBlock()
    {
        File.WriteAllText(Path.Combine(_baseDir, "mcp.json"), """{ "mcpServers": { "echo": { "command": "npx" } } }""");

        using var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp.json", "Test", NullLogger.Instance);

        block.Should().NotBeNull();
        block!.Value.ServersElement.EnumerateObject().Should().ContainSingle(p => p.Name == "echo");
    }

    [Fact]
    public void ReadMcpServersBlock_McpServersPropertyIsAString_ReturnsNullWithoutThrowing()
    {
        // Regression test: "mcpServers" parses as valid JSON but the wrong shape. Every caller enumerates
        // ServersElement as an object's properties, which throws InvalidOperationException on any other
        // JsonValueKind if this guard is missing — that exception must never escape this method.
        File.WriteAllText(Path.Combine(_baseDir, "mcp.json"), """{ "mcpServers": "not-an-object" }""");

        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_McpServersPropertyIsAnArray_ReturnsNullWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_baseDir, "mcp.json"), """{ "mcpServers": [] }""");

        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_RelativePathContainsInvalidCharacter_ReturnsNullWithoutThrowing()
    {
        // Regression test: Path.Combine/Path.GetFullPath throws ArgumentException for an embedded NUL —
        // this used to propagate straight out of ReadMcpServersBlock, uncaught, before the file-existence
        // check ever ran.
        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp\0.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_PathEscapesBaseDirectory_ReturnsNull()
    {
        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "../../escape.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_FileMissing_ReturnsNull()
    {
        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./missing.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_MalformedJson_ReturnsNullWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_baseDir, "mcp.json"), "{ this is not valid json");

        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }

    [Fact]
    public void ReadMcpServersBlock_NoMcpServersProperty_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_baseDir, "mcp.json"), """{ "somethingElse": true }""");

        var block = McpManifestReader.ReadMcpServersBlock(_baseDir, "./mcp.json", "Test", NullLogger.Instance);

        block.Should().BeNull();
    }
}
