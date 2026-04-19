using Domain.AI.MCP;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.MCP;

/// <summary>
/// Tests for <see cref="McpResource"/> and <see cref="McpResourceContent"/> records.
/// </summary>
public sealed class McpResourceTests
{
    [Fact]
    public void McpResource_Constructor_RequiredOnly()
    {
        var resource = new McpResource("trace://run-1/output.json", "output.json");

        resource.Uri.Should().Be("trace://run-1/output.json");
        resource.Name.Should().Be("output.json");
        resource.Description.Should().BeNull();
        resource.MimeType.Should().Be("text/plain");
    }

    [Fact]
    public void McpResource_Constructor_AllParameters()
    {
        var resource = new McpResource(
            "trace://run-1/output.json",
            "output.json",
            "Task output",
            "application/json");

        resource.Description.Should().Be("Task output");
        resource.MimeType.Should().Be("application/json");
    }

    [Fact]
    public void McpResource_Equality_SameValues_AreEqual()
    {
        var r1 = new McpResource("uri", "name", "desc", "text/plain");
        var r2 = new McpResource("uri", "name", "desc", "text/plain");

        r1.Should().Be(r2);
    }

    [Fact]
    public void McpResourceContent_Constructor_RequiredOnly()
    {
        var content = new McpResourceContent("trace://run-1/output.json", "file content");

        content.Uri.Should().Be("trace://run-1/output.json");
        content.Text.Should().Be("file content");
        content.MimeType.Should().Be("text/plain");
    }

    [Fact]
    public void McpResourceContent_Constructor_WithMimeType()
    {
        var content = new McpResourceContent(
            "trace://run-1/output.json",
            "{\"key\": \"value\"}",
            "application/json");

        content.MimeType.Should().Be("application/json");
    }

    [Fact]
    public void McpResourceContent_Equality_SameValues_AreEqual()
    {
        var c1 = new McpResourceContent("uri", "text", "text/plain");
        var c2 = new McpResourceContent("uri", "text", "text/plain");

        c1.Should().Be(c2);
    }
}
