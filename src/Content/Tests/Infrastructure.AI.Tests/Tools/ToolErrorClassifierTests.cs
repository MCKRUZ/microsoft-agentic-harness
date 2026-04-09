using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public sealed class ToolErrorClassifierTests
{
    [Fact]
    public void Classify_OperationCanceledException_ReturnsTimeout()
    {
        ToolErrorClassifier.Classify(new OperationCanceledException())
            .Should().Be("timeout");
    }

    [Fact]
    public void Classify_TaskCanceledException_ReturnsTimeout()
    {
        // TaskCanceledException derives from OperationCanceledException
        ToolErrorClassifier.Classify(new TaskCanceledException())
            .Should().Be("timeout");
    }

    [Fact]
    public void Classify_UnauthorizedAccessException_ReturnsPermissionDenied()
    {
        ToolErrorClassifier.Classify(new UnauthorizedAccessException())
            .Should().Be("permission_denied");
    }

    [Fact]
    public void Classify_FileNotFoundException_ReturnsNotFound()
    {
        ToolErrorClassifier.Classify(new FileNotFoundException())
            .Should().Be("not_found");
    }

    [Fact]
    public void Classify_KeyNotFoundException_ReturnsNotFound()
    {
        ToolErrorClassifier.Classify(new KeyNotFoundException())
            .Should().Be("not_found");
    }

    [Fact]
    public void Classify_ArgumentException_ReturnsInvalidInput()
    {
        ToolErrorClassifier.Classify(new ArgumentException("bad arg"))
            .Should().Be("invalid_input");
    }

    [Fact]
    public void Classify_ArgumentNullException_ReturnsInvalidInput()
    {
        // ArgumentNullException derives from ArgumentException
        ToolErrorClassifier.Classify(new ArgumentNullException("param"))
            .Should().Be("invalid_input");
    }

    [Fact]
    public void Classify_GenericException_ReturnsInternalError()
    {
        ToolErrorClassifier.Classify(new InvalidOperationException("boom"))
            .Should().Be("internal_error");
    }

    [Fact]
    public void Classify_NullReferenceException_ReturnsInternalError()
    {
        ToolErrorClassifier.Classify(new NullReferenceException())
            .Should().Be("internal_error");
    }
}
