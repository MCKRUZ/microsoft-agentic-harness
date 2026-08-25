using Application.AI.Common.Models.Conversations;
using FluentAssertions;

namespace Application.AI.Common.Tests.Models.Conversations;

/// <summary>
/// Pins <see cref="ConversationMessage.ToolCalls"/>' empty-to-null normalization across every way a
/// value can reach it (#510).
/// </summary>
/// <remarks>
/// <para>
/// The guarantee is load-bearing, not cosmetic. Two call sites cite it as their reason for not
/// normalizing themselves — <c>ConversationEntityMapper.ToEntity</c> and
/// <c>EfCoreConversationStore.GetHistoryForDispatch</c>'s dispatch-window filter — because an empty
/// non-null list serializes to a non-null <c>"[]"</c> column that filter would read as "this row is
/// model-relevant," admitting an empty-content widget row into the prompt window.
/// </para>
/// <para>
/// The <c>with</c>-expression case is the one that was actually broken: a property <em>initializer</em>
/// runs only in the primary constructor, and a record's compiler-generated copy constructor copies
/// backing fields directly without re-running it. The fix moved the normalization into the property's
/// <c>init</c> accessor, which every assignment path goes through.
/// </para>
/// <para>
/// <see cref="Construction_NonEmptyList_IsPreserved"/> is not padding either: the fix depends on the
/// backing field's initializer binding to the primary constructor <em>parameter</em> rather than to the
/// property of the same name. Binding the other way would compile and silently null every tool-call
/// list in the system, so it is pinned rather than assumed.
/// </para>
/// </remarks>
public sealed class ConversationMessageTests
{
    private static ToolCallRecord AnyCall() => new("file_read", "{}", "ok", DurationMs: 0);

    private static ConversationMessage Message(IReadOnlyList<ToolCallRecord>? toolCalls) =>
        new(Guid.NewGuid(), MessageRole.Assistant, "text", DateTimeOffset.UtcNow, toolCalls);

    [Fact]
    public void Construction_NonEmptyList_IsPreserved()
    {
        var call = AnyCall();

        Message([call]).ToolCalls.Should().ContainSingle().Which.Should().BeSameAs(call);
    }

    [Fact]
    public void Construction_EmptyList_NormalizesToNull()
    {
        Message([]).ToolCalls.Should().BeNull();
    }

    [Fact]
    public void Construction_Null_StaysNull()
    {
        Message(null).ToolCalls.Should().BeNull();
    }

    [Fact]
    public void WithExpression_EmptyList_NormalizesToNull()
    {
        var original = Message([AnyCall()]);

        var copied = original with { ToolCalls = [] };

        copied.ToolCalls.Should().BeNull(
            "a 'with' expression assigns through the init accessor, which must normalize the same way " +
            "the primary constructor does — otherwise the empty list serializes to a non-null \"[]\" " +
            "column that the dispatch-window filter reads as a model-relevant row");
    }

    [Fact]
    public void WithExpression_NonEmptyList_IsPreserved()
    {
        var replacement = AnyCall();

        var copied = Message(null) with { ToolCalls = [replacement] };

        copied.ToolCalls.Should().ContainSingle().Which.Should().BeSameAs(replacement);
    }

    [Fact]
    public void WithExpression_UnrelatedProperty_CarriesToolCallsThrough()
    {
        var call = AnyCall();
        var original = Message([call]);

        var copied = original with { Content = "edited" };

        copied.ToolCalls.Should().ContainSingle().Which.Should().BeSameAs(call,
            "the copy constructor copies the backing field, so a copy that doesn't touch ToolCalls " +
            "must keep them");
    }

    [Fact]
    public void ObjectInitializer_EmptyList_NormalizesToNull()
    {
        var message = new ConversationMessage(
            Guid.NewGuid(), MessageRole.Assistant, "text", DateTimeOffset.UtcNow)
        {
            ToolCalls = [],
        };

        message.ToolCalls.Should().BeNull();
    }
}
