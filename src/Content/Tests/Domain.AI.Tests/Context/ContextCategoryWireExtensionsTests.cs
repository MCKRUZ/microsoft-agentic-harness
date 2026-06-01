using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Pin the lowercase wire contract for <see cref="ContextCategory"/>. This is
/// the single source of truth consumed by Infrastructure (Postgres snapshot
/// serialization) and Presentation (SignalR DTO mapping); the frontend
/// <c>CategoryKey</c> string literal must stay in lockstep.
/// </summary>
public sealed class ContextCategoryWireExtensionsTests
{
    [Theory]
    [InlineData(ContextCategory.System, "system")]
    [InlineData(ContextCategory.Agents, "agents")]
    [InlineData(ContextCategory.Skills, "skills")]
    [InlineData(ContextCategory.Tools, "tools")]
    [InlineData(ContextCategory.Mcp, "mcp")]
    [InlineData(ContextCategory.Messages, "messages")]
    public void ToWire_emits_lowercase_canonical_string(ContextCategory category, string expected)
    {
        category.ToWire().Should().Be(expected);
    }

    [Fact]
    public void ToWire_throws_on_undefined_enum_value()
    {
        var bogus = (ContextCategory)999;
        var act = () => bogus.ToWire();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("system", ContextCategory.System)]
    [InlineData("agents", ContextCategory.Agents)]
    [InlineData("skills", ContextCategory.Skills)]
    [InlineData("tools", ContextCategory.Tools)]
    [InlineData("mcp", ContextCategory.Mcp)]
    [InlineData("messages", ContextCategory.Messages)]
    public void FromWire_round_trips_every_category(string wire, ContextCategory expected)
    {
        ContextCategoryWireExtensions.FromWire(wire).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("future-category")]
    [InlineData("SYSTEM")]
    public void FromWire_unknown_value_falls_back_to_System_for_forward_compat(string wire)
    {
        ContextCategoryWireExtensions.FromWire(wire).Should().Be(ContextCategory.System);
    }

    [Fact]
    public void ToWire_then_FromWire_round_trips_every_enum_value()
    {
        foreach (var value in Enum.GetValues<ContextCategory>())
        {
            ContextCategoryWireExtensions.FromWire(value.ToWire()).Should().Be(value);
        }
    }
}
