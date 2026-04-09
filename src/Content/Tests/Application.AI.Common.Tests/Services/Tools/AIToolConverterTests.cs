using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

public class AIToolConverterTests
{
    private readonly AIToolConverter _converter;

    public AIToolConverterTests()
    {
        _converter = new AIToolConverter(NullLogger<AIToolConverter>.Instance);
    }

    [Fact]
    public void Priority_Returns200()
    {
        _converter.Priority.Should().Be(200);
    }

    [Fact]
    public void CanConvert_AnyTool_ReturnsTrue()
    {
        var tool = CreateMockTool("test_tool", ["read", "write"]);

        _converter.CanConvert(tool).Should().BeTrue();
    }

    [Fact]
    public void Convert_WithAllOperations_ExposesAll()
    {
        var tool = CreateMockTool("file_system", ["read", "write", "list"]);

        var result = _converter.Convert(tool);

        result.Should().NotBeNull();
        result!.Name.Should().Be("file_system");
    }

    [Fact]
    public void Convert_WithAllowedOperations_FiltersCorrectly()
    {
        var tool = CreateMockTool("file_system", ["read", "write", "list", "delete"]);
        var allowed = new List<string> { "read", "list" };

        var result = _converter.Convert(tool, allowed);

        result.Should().NotBeNull();
        result!.Name.Should().Be("file_system");
    }

    [Fact]
    public void Convert_AllowedOperationsEmpty_ExposesAll()
    {
        var tool = CreateMockTool("calculator", ["add", "subtract"]);

        var result = _converter.Convert(tool, new List<string>());

        result.Should().NotBeNull();
    }

    [Fact]
    public void Convert_AllowedOperationsNull_ExposesAll()
    {
        var tool = CreateMockTool("calculator", ["add", "subtract"]);

        var result = _converter.Convert(tool, null);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Convert_NoMatchingOperations_ReturnsNull()
    {
        var tool = CreateMockTool("file_system", ["read", "write"]);
        var allowed = new List<string> { "delete", "purge" };

        var result = _converter.Convert(tool, allowed);

        result.Should().BeNull();
    }

    [Fact]
    public void Convert_CaseInsensitiveOperationFiltering_Works()
    {
        var tool = CreateMockTool("file_system", ["Read", "Write"]);
        var allowed = new List<string> { "read", "write" };

        var result = _converter.Convert(tool, allowed);

        result.Should().NotBeNull();
    }

    private static ITool CreateMockTool(string name, IReadOnlyList<string> operations)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns($"Tool: {name}");
        mock.Setup(t => t.SupportedOperations).Returns(operations);
        mock.Setup(t => t.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("success"));
        return mock.Object;
    }
}
