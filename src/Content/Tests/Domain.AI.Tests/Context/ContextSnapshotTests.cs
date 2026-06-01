using Domain.AI.Context;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Context;

/// <summary>
/// Tests for <see cref="ContextSnapshot"/> and <see cref="LoadedItem"/> —
/// construction, immutability, equality.
/// </summary>
public sealed class ContextSnapshotTests
{
    [Fact]
    public void ContextSnapshot_Construction_SetsAllProperties()
    {
        var captured = new DateTimeOffset(2026, 6, 1, 12, 34, 56, TimeSpan.Zero);
        var loaded = new[]
        {
            new LoadedItem("User message", 520, ContextCategory.Messages, null),
            new LoadedItem("Tool: Read · plan.md", 1200, ContextCategory.Messages, "plan.md"),
        };
        var ctxAfter = new CategoryBreakdown(8200, 0, 0, 3500, 0, 17000);

        var snapshot = new ContextSnapshot(
            ConversationId: "conv-1",
            TurnIndex: 4,
            TurnId: "t-04",
            CtxAfter: ctxAfter,
            Loaded: loaded,
            CapturedAtUtc: captured);

        snapshot.ConversationId.Should().Be("conv-1");
        snapshot.TurnIndex.Should().Be(4);
        snapshot.TurnId.Should().Be("t-04");
        snapshot.CtxAfter.Should().BeSameAs(ctxAfter);
        snapshot.CtxAfter.Total.Should().Be(28_700);
        snapshot.Loaded.Should().HaveCount(2);
        snapshot.CapturedAtUtc.Should().Be(captured);
    }

    [Fact]
    public void LoadedItem_OptionalReference_DefaultsToNull()
    {
        var item = new LoadedItem("System prompt", 5000, ContextCategory.System, null);

        item.Reference.Should().BeNull();
        item.What.Should().Be("System prompt");
        item.Tokens.Should().Be(5000);
        item.Category.Should().Be(ContextCategory.System);
    }

    [Fact]
    public void ContextSnapshot_Equality_HoldsByValue()
    {
        var captured = DateTimeOffset.UtcNow;
        var a = new ContextSnapshot("c1", 0, "t-00", CategoryBreakdown.Empty, [], captured);
        var b = new ContextSnapshot("c1", 0, "t-00", CategoryBreakdown.Empty, [], captured);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
