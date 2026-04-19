using Domain.AI.Compaction;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Compaction;

/// <summary>
/// Tests for <see cref="CompactionResult"/> record — factory methods and property values.
/// </summary>
public sealed class CompactionResultTests
{
    [Fact]
    public void Succeeded_SetsSuccessTrue_WithBoundary()
    {
        var boundary = new CompactionBoundaryMessage
        {
            Id = "b-1",
            Trigger = CompactionTrigger.Manual,
            Strategy = CompactionStrategy.Partial,
            PreCompactionTokens = 5000,
            PostCompactionTokens = 2000,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = "Summary"
        };

        var result = CompactionResult.Succeeded(boundary);

        result.Success.Should().BeTrue();
        result.Boundary.Should().BeSameAs(boundary);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failed_SetsSuccessFalse_WithError()
    {
        var result = CompactionResult.Failed("LLM call timed out");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("LLM call timed out");
        result.Boundary.Should().BeNull();
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var result = CompactionResult.Failed("error");
        var updated = result with { Error = "different error" };

        updated.Error.Should().Be("different error");
        result.Error.Should().Be("error");
    }
}
