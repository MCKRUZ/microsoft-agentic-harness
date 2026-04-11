using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
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

    // --- Invocation regression tests for parametersJson parsing bug ---
    // Before the fix, the LLM passing parametersJson as a JSON object (not a string)
    // caused ParseParameters to receive null, so the tool got an empty dictionary
    // and failed with "Invalid path or parameters." on every call.

    [Fact]
    public async Task InvokeAsync_ParametersJsonAsObject_ToolReceivesCorrectParameters()
    {
        // Arrange — LLM sends parametersJson as a JSON object (the common failure case)
        IReadOnlyDictionary<string, object?>? captured = null;
        var tool = CreateCapturingTool("file_system", ["search"], (op, p, ct) =>
        {
            captured = p;
            return Task.FromResult(ToolResult.Ok("ok"));
        });

        var aiFunction = (AIFunction)_converter.Convert(tool)!;

        var paramsAsElement = JsonSerializer.SerializeToElement(
            new { path = "src", search_term = "ExecuteAgentTurnCommand" });

        var args = new AIFunctionArguments
        {
            ["operation"] = "search",
            ["parametersJson"] = paramsAsElement
        };

        // Act
        await aiFunction.InvokeAsync(args);

        // Assert — tool must have received path and search_term
        captured.Should().NotBeNull();
        captured.Should().ContainKey("path").WhoseValue.Should().Be("src");
        captured.Should().ContainKey("search_term").WhoseValue.Should().Be("ExecuteAgentTurnCommand");
    }

    [Fact]
    public async Task InvokeAsync_ParametersJsonAsDoubleEncodedString_ToolReceivesCorrectParameters()
    {
        // Arrange — LLM double-encodes parametersJson as a JSON string
        IReadOnlyDictionary<string, object?>? captured = null;
        var tool = CreateCapturingTool("file_system", ["read"], (op, p, ct) =>
        {
            captured = p;
            return Task.FromResult(ToolResult.Ok("ok"));
        });

        var aiFunction = (AIFunction)_converter.Convert(tool)!;
        var innerJson = JsonSerializer.Serialize(new { path = "src/MyFile.cs" });
        // Wrap as a JSON string element
        var paramsAsStringElement = JsonSerializer.SerializeToElement(innerJson);

        var args = new AIFunctionArguments
        {
            ["operation"] = "read",
            ["parametersJson"] = paramsAsStringElement
        };

        // Act
        await aiFunction.InvokeAsync(args);

        // Assert
        captured.Should().NotBeNull();
        captured.Should().ContainKey("path").WhoseValue.Should().Be("src/MyFile.cs");
    }

    [Fact]
    public async Task InvokeAsync_NullParametersJson_ToolReceivesEmptyDictionary()
    {
        // Arrange
        IReadOnlyDictionary<string, object?>? captured = null;
        var tool = CreateCapturingTool("file_system", ["list"], (op, p, ct) =>
        {
            captured = p;
            return Task.FromResult(ToolResult.Ok("ok"));
        });

        var aiFunction = (AIFunction)_converter.Convert(tool)!;
        var args = new AIFunctionArguments
        {
            ["operation"] = "list",
            ["parametersJson"] = null
        };

        // Act
        await aiFunction.InvokeAsync(args);

        // Assert — must not throw; empty dictionary is acceptable
        captured.Should().NotBeNull();
        captured.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_UnknownOperation_ReturnsErrorWithoutCallingTool()
    {
        // Arrange
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns("file_system");
        mock.Setup(t => t.Description).Returns("desc");
        mock.Setup(t => t.SupportedOperations).Returns(["read"]);

        var aiFunction = (AIFunction)_converter.Convert(mock.Object)!;
        var args = new AIFunctionArguments
        {
            ["operation"] = "delete",
            ["parametersJson"] = null
        };

        // Act
        var result = await aiFunction.InvokeAsync(args);

        // Assert — AIFunction wraps the return value in a JsonElement
        var resultStr = result is System.Text.Json.JsonElement je ? je.GetString() : result?.ToString();
        resultStr.Should().Contain("not available");
        mock.Verify(t => t.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, object?>>(),
            It.IsAny<CancellationToken>()), Times.Never);
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

    private static ITool CreateCapturingTool(
        string name,
        IReadOnlyList<string> operations,
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<ToolResult>> handler)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns($"Tool: {name}");
        mock.Setup(t => t.SupportedOperations).Returns(operations);
        mock.Setup(t => t.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyDictionary<string, object?>, CancellationToken>(handler);
        return mock.Object;
    }
}
