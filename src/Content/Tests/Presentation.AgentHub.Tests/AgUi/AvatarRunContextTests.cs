using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Tests for <see cref="AvatarRunContext.TryParse"/> — the defensive parser for
/// <see cref="RunAgentInput.Context"/>'s avatar-specific payload.
/// </summary>
public sealed class AvatarRunContextTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void NullContext_ReturnsBothNull()
    {
        var result = AvatarRunContext.TryParse(null);

        result.TurnContext.Should().BeNull();
        result.DeploymentOverride.Should().BeNull();
    }

    [Fact]
    public void BothFieldsPresent_ParsesBoth()
    {
        var context = Parse("""{"turnContext":"Mood: relaxed.","deploymentOverride":"euryale-70b"}""");

        var result = AvatarRunContext.TryParse(context);

        result.TurnContext.Should().Be("Mood: relaxed.");
        result.DeploymentOverride.Should().Be("euryale-70b");
    }

    [Fact]
    public void OnlyTurnContextPresent_DeploymentOverrideIsNull()
    {
        var context = Parse("""{"turnContext":"Mood: relaxed."}""");

        var result = AvatarRunContext.TryParse(context);

        result.TurnContext.Should().Be("Mood: relaxed.");
        result.DeploymentOverride.Should().BeNull();
    }

    [Fact]
    public void EmptyObject_ReturnsBothNull()
    {
        var result = AvatarRunContext.TryParse(Parse("{}"));

        result.TurnContext.Should().BeNull();
        result.DeploymentOverride.Should().BeNull();
    }

    [Fact]
    public void NotAnObject_ReturnsBothNullRatherThanThrowing()
    {
        var result = AvatarRunContext.TryParse(Parse("\"just a string\""));

        result.TurnContext.Should().BeNull();
        result.DeploymentOverride.Should().BeNull();
    }

    [Fact]
    public void WrongTypeForField_TreatedAsAbsent()
    {
        // A caller that sends turnContext as a number rather than a string must not crash the run —
        // this is exactly the "server is authoritative, accept but tolerate" contract the rest of
        // RunAgentInput's unused fields already follow.
        var context = Parse("""{"turnContext":42}""");

        var result = AvatarRunContext.TryParse(context);

        result.TurnContext.Should().BeNull();
    }
}
