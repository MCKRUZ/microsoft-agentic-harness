using Application.AI.Common.Services;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// The one restore-on-dispose implementation behind eleven ambient accessors (#550). Asserted inside a
/// single synchronous frame on purpose: an <see cref="AsyncLocal{T}"/> write made inside an awaited
/// method never flows back to the caller, so a post-await read here would pass whether or not the handle
/// restored anything (see <c>GovernanceAccessorScopeTests</c>).
/// </summary>
public sealed class AmbientScopeTests
{
    private static readonly AsyncLocal<string?> Slot = new();

    [Fact]
    public void Begin_PublishesTheValue_AndDisposeRestoresWhatWasThere()
    {
        Slot.Value = null;

        using (AmbientScope.Begin(Slot, "inner"))
            Slot.Value.Should().Be("inner");

        Slot.Value.Should().BeNull();
    }

    [Fact]
    public void Begin_Nested_EachDisposeRestoresTheEnclosingValueNotNull()
    {
        Slot.Value = "outer";

        var middle = AmbientScope.Begin(Slot, "middle");
        var inner = AmbientScope.Begin(Slot, "inner");
        Slot.Value.Should().Be("inner");

        inner.Dispose();
        Slot.Value.Should().Be("middle", "an inner scope ending must put the enclosing scope's value back");

        middle.Dispose();
        Slot.Value.Should().Be("outer");
        Slot.Value = null;
    }

    [Fact]
    public void Dispose_CalledTwice_RestoresOnce()
    {
        Slot.Value = "outer";
        var scope = AmbientScope.Begin(Slot, "inner");
        scope.Dispose();

        // Something else publishes after the scope ended. A second dispose must not clobber it with the
        // value the first one already restored.
        Slot.Value = "published-later";
        scope.Dispose();

        Slot.Value.Should().Be("published-later");
        Slot.Value = null;
    }

    [Fact]
    public void Begin_NullValue_IsPublishedAsNull()
    {
        Slot.Value = "outer";

        using (AmbientScope.Begin(Slot, null))
            Slot.Value.Should().BeNull();

        Slot.Value.Should().Be("outer");
        Slot.Value = null;
    }
}
