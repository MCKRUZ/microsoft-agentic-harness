using Application.AI.Common.Interfaces.Connectors;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Connectors;

/// <summary>
/// Tests for <see cref="ConnectorOperationResult"/> covering factory methods,
/// property defaults, and record behavior.
/// </summary>
public class ConnectorOperationResultTests
{
    [Fact]
    public void Success_WithDefaults_ReturnsSuccessResult()
    {
        var result = ConnectorOperationResult.Success();

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
        result.MarkdownResult.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.HttpStatusCode.Should().BeNull();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Success_WithData_SetsDataProperty()
    {
        var data = new { Id = 1, Name = "test" };
        var result = ConnectorOperationResult.Success(data: data);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeSameAs(data);
    }

    [Fact]
    public void Success_WithMarkdown_SetsMarkdownResult()
    {
        var result = ConnectorOperationResult.Success(markdown: "## Issues\n- Bug #1");

        result.MarkdownResult.Should().Be("## Issues\n- Bug #1");
    }

    [Fact]
    public void Success_WithDataAndMarkdown_SetsBoth()
    {
        var data = new { Count = 5 };
        var result = ConnectorOperationResult.Success(data, "5 items found");

        result.Data.Should().BeSameAs(data);
        result.MarkdownResult.Should().Be("5 items found");
    }

    [Fact]
    public void Failure_SetsErrorMessage()
    {
        var result = ConnectorOperationResult.Failure("Not found");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Not found");
        result.Data.Should().BeNull();
        result.HttpStatusCode.Should().BeNull();
    }

    [Fact]
    public void Failure_WithHttpStatusCode_SetsBothProperties()
    {
        var result = ConnectorOperationResult.Failure("Not found", 404);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Not found");
        result.HttpStatusCode.Should().Be(404);
    }

    [Fact]
    public void ExecutedAt_DefaultsToUtcNow()
    {
        var before = DateTime.UtcNow;
        var result = ConnectorOperationResult.Success();
        var after = DateTime.UtcNow;

        result.ExecutedAt.Should().BeOnOrAfter(before);
        result.ExecutedAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = ConnectorOperationResult.Success(markdown: "original");
        var modified = original with { MarkdownResult = "modified" };

        modified.MarkdownResult.Should().Be("modified");
        original.MarkdownResult.Should().Be("original");
    }

    [Fact]
    public void Metadata_CanBeSetViaInitProperty()
    {
        var result = new ConnectorOperationResult
        {
            IsSuccess = true,
            Metadata = new Dictionary<string, object> { ["key"] = "value" }
        };

        result.Metadata.Should().ContainKey("key");
    }
}
