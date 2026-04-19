using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Models.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Models.Tools;

/// <summary>
/// Tests for <see cref="ToolExecutionRequest"/> covering construction,
/// required properties, and record equality.
/// </summary>
public class ToolExecutionRequestTests
{
    private static Mock<ITool> CreateMockTool(string name = "test_tool")
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns("Test tool");
        mock.Setup(t => t.SupportedOperations).Returns(new[] { "read", "write" });
        return mock;
    }

    [Fact]
    public void ConstructsWithRequiredProperties()
    {
        var tool = CreateMockTool().Object;
        var parameters = new Dictionary<string, object?> { ["path"] = "/tmp" };

        var request = new ToolExecutionRequest
        {
            Tool = tool,
            Operation = "read",
            Parameters = parameters,
            CallId = "call-1"
        };

        request.Tool.Should().BeSameAs(tool);
        request.Operation.Should().Be("read");
        request.Parameters.Should().ContainKey("path");
        request.CallId.Should().Be("call-1");
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var tool = CreateMockTool().Object;
        var original = new ToolExecutionRequest
        {
            Tool = tool,
            Operation = "read",
            Parameters = new Dictionary<string, object?>(),
            CallId = "call-1"
        };

        var modified = original with { Operation = "write" };

        modified.Operation.Should().Be("write");
        modified.CallId.Should().Be("call-1");
        original.Operation.Should().Be("read");
    }

    [Fact]
    public void Equality_DifferentInstances_SameValues_AreEqual()
    {
        var tool = CreateMockTool().Object;
        var parameters = new Dictionary<string, object?>();

        var a = new ToolExecutionRequest
        {
            Tool = tool,
            Operation = "read",
            Parameters = parameters,
            CallId = "call-1"
        };
        var b = new ToolExecutionRequest
        {
            Tool = tool,
            Operation = "read",
            Parameters = parameters,
            CallId = "call-1"
        };

        a.Should().Be(b);
    }
}
