using Application.AI.Common.Exceptions;
using Application.Common.Exceptions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Exceptions;

/// <summary>
/// Tests for <see cref="McpConnectionException"/> covering all constructors,
/// property assignments, message formatting, and argument validation.
/// </summary>
public class McpConnectionExceptionTests
{
    [Fact]
    public void DefaultCtor_SetsDefaultMessage()
    {
        var ex = new McpConnectionException();

        ex.Message.Should().Be("Failed to connect to an MCP server.");
        ex.ServerName.Should().BeNull();
        ex.Transport.Should().BeNull();
    }

    [Fact]
    public void MessageCtor_SetsCustomMessage()
    {
        var ex = new McpConnectionException("connection refused");

        ex.Message.Should().Be("connection refused");
    }

    [Fact]
    public void MessageAndInnerCtor_SetsMessageAndInner()
    {
        var inner = new Exception("timeout");
        var ex = new McpConnectionException("failed", inner);

        ex.Message.Should().Be("failed");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void StructuredCtor_WithTransport_FormatsMessage()
    {
        var ex = new McpConnectionException("local-tools", "stdio");

        ex.Message.Should().Be("Failed to connect to MCP server 'local-tools' via 'stdio' transport.");
        ex.ServerName.Should().Be("local-tools");
        ex.Transport.Should().Be("stdio");
    }

    [Fact]
    public void StructuredCtor_WithNullTransport_FormatsWithoutTransport()
    {
        var ex = new McpConnectionException(serverName: "remote-search", transport: null);

        ex.Message.Should().Be("Failed to connect to MCP server 'remote-search'.");
        ex.ServerName.Should().Be("remote-search");
        ex.Transport.Should().BeNull();
    }

    [Fact]
    public void StructuredCtor_WithInnerException_PreservesInner()
    {
        var inner = new HttpRequestException("503");
        var ex = new McpConnectionException("server", "http", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StructuredCtor_NullOrWhitespaceServerName_Throws(string? serverName)
    {
        var act = () => new McpConnectionException(serverName!, "http");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsApplicationExceptionBase()
    {
        var ex = new McpConnectionException();
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}
