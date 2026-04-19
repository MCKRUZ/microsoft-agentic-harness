using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="ToolDescriptionBuilder"/> covering fluent API methods,
/// newline handling, implicit string conversion, and edge cases.
/// </summary>
public class ToolDescriptionBuilderTests
{
    [Fact]
    public void Build_EmptyBuilder_ReturnsEmptyString()
    {
        var builder = new ToolDescriptionBuilder();

        builder.Build().Should().BeEmpty();
    }

    [Fact]
    public void AddPurpose_SetsPurposeText()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Reads files from disk.")
            .Build();

        result.Should().Be("Reads files from disk.");
    }

    [Fact]
    public void AddPurpose_NullOrWhitespace_DoesNotAppend()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("")
            .AddPurpose("   ")
            .Build();

        result.Should().BeEmpty();
    }

    [Fact]
    public void AddSection_AppendsHeaderAndContent()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Purpose")
            .AddSection("Usage", "Call with file path")
            .Build();

        result.Should().Contain("Usage: Call with file path");
    }

    [Fact]
    public void AddSection_NullOrWhitespaceContent_DoesNotAppend()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Purpose")
            .AddSection("Usage", "")
            .Build();

        result.Should().Be("Purpose");
    }

    [Fact]
    public void AddOperations_ListsOperations()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("File tool")
            .AddOperations(["read", "write", "list"])
            .Build();

        result.Should().Contain("Supported operations: read, write, list");
    }

    [Fact]
    public void AddOperations_EmptyList_DoesNotAppend()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Tool")
            .AddOperations([])
            .Build();

        result.Should().Be("Tool");
    }

    [Fact]
    public void AddOperations_Null_DoesNotAppend()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Tool")
            .AddOperations(null!)
            .Build();

        result.Should().Be("Tool");
    }

    [Fact]
    public void AddParameter_Required_FormatsCorrectly()
    {
        var result = new ToolDescriptionBuilder()
            .AddParameter("path", required: true, "File path to read")
            .Build();

        result.Should().Contain("- path (required): File path to read");
    }

    [Fact]
    public void AddParameter_Optional_FormatsCorrectly()
    {
        var result = new ToolDescriptionBuilder()
            .AddParameter("encoding", required: false)
            .Build();

        result.Should().Contain("- encoding (optional)");
        result.Should().NotContain(":");
    }

    [Fact]
    public void AddParameters_MultipleParams_FormatsAll()
    {
        var result = new ToolDescriptionBuilder()
            .AddParameters(
                ("path", true, "File path"),
                ("encoding", false, null))
            .Build();

        result.Should().Contain("- path (required): File path");
        result.Should().Contain("- encoding (optional)");
    }

    [Fact]
    public void AddNote_AppendsNotePrefix()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Tool")
            .AddNote("Requires admin access.")
            .Build();

        result.Should().Contain("Note: Requires admin access.");
    }

    [Fact]
    public void AddNote_NullOrWhitespace_DoesNotAppend()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Tool")
            .AddNote("")
            .Build();

        result.Should().Be("Tool");
    }

    [Fact]
    public void FluentChaining_ProducesCompleteDescription()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("File system operations.")
            .AddOperations(["read", "write"])
            .AddParameter("path", true, "Target file path")
            .AddNote("Paths are relative to workspace root.")
            .Build();

        result.Should().Contain("File system operations.");
        result.Should().Contain("Supported operations: read, write");
        result.Should().Contain("- path (required): Target file path");
        result.Should().Contain("Note: Paths are relative to workspace root.");
    }

    [Fact]
    public void ImplicitStringConversion_ReturnsBuildResult()
    {
        ToolDescriptionBuilder builder = new ToolDescriptionBuilder()
            .AddPurpose("Test tool");

        string result = builder;

        result.Should().Be("Test tool");
    }

    [Fact]
    public void EnsureNewLine_AddedBetweenSections()
    {
        var result = new ToolDescriptionBuilder()
            .AddPurpose("Purpose")
            .AddSection("Section", "Content")
            .Build();

        var lines = result.Split(Environment.NewLine);
        lines.Should().HaveCountGreaterThan(1);
    }
}
