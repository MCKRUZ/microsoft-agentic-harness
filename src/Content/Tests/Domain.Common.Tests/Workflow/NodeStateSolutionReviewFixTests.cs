using System.Text.Json;
using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Regression tests for the solution review finding that <see cref="NodeState.GetMetadata{T}"/>
/// silently returned the default for every metadata value after a System.Text.Json checkpoint
/// reload, because reloaded values come back boxed as <see cref="JsonElement"/> (which is not
/// <c>T</c> for any primitive and does not implement <see cref="IConvertible"/>, so
/// <see cref="Convert.ChangeType(object, Type)"/> threw and the blanket catch swallowed it).
/// </summary>
public class NodeStateSolutionReviewFixTests
{
    private static NodeState RoundTripThroughJson(NodeState node)
    {
        var json = JsonSerializer.Serialize(node);
        return JsonSerializer.Deserialize<NodeState>(json)!;
    }

    [Fact]
    public void GetMetadata_IntValueAfterJsonRoundtrip_ReturnsOriginalValue()
    {
        var node = new NodeState();
        node.SetMetadata("validation_score", 92);

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata<int>("validation_score").Should().Be(92);
    }

    [Fact]
    public void GetMetadata_StringValueAfterJsonRoundtrip_ReturnsOriginalValue()
    {
        var node = new NodeState();
        node.SetMetadata("error_message", "boom");

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata<string>("error_message").Should().Be("boom");
    }

    [Fact]
    public void GetMetadata_BoolValueAfterJsonRoundtrip_ReturnsOriginalValue()
    {
        var node = new NodeState();
        node.SetMetadata("decision", true);

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata<bool>("decision").Should().BeTrue();
    }

    [Fact]
    public void GetMetadata_DoubleValueAfterJsonRoundtrip_ReturnsOriginalValue()
    {
        var node = new NodeState();
        node.SetMetadata("duration_seconds", 12.5d);

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata<double>("duration_seconds").Should().Be(12.5d);
    }

    [Fact]
    public void GetMetadata_MissingKeyAfterJsonRoundtrip_ReturnsProvidedDefault()
    {
        var node = new NodeState();
        node.SetMetadata("critical_issues", 3);

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata("absent_key", -1).Should().Be(-1);
    }

    [Fact]
    public void GetMetadata_InMemoryValue_StillReturnsValueWithoutConversion()
    {
        var node = new NodeState();
        node.SetMetadata("validation_score", 75);

        node.GetMetadata<int>("validation_score").Should().Be(75);
    }

    [Fact]
    public void GetMetadata_ConvertibleNumericWidening_StillConverts()
    {
        var node = new NodeState();
        node.SetMetadata("token_count", 1000);

        // int boxed in-memory requested as long must still widen via IConvertible.
        node.GetMetadata<long>("token_count").Should().Be(1000L);
    }

    [Fact]
    public void GetMetadata_UnrepresentableValueAfterJsonRoundtrip_ReturnsDefault()
    {
        var node = new NodeState();
        node.SetMetadata("error_message", "not-a-number");

        var reloaded = RoundTripThroughJson(node);

        reloaded.GetMetadata("error_message", -1).Should().Be(-1);
    }
}
