using Application.AI.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="ToolCeilingResolver"/>. These encode the load-bearing security invariant
/// of the agent tool ceiling: applying a ceiling can only ever <em>tighten</em> the allowlist — it can
/// never grant a tool the current allowlist did not already permit, and chaining ceilings can never
/// re-grant a tool a prior intersection had already denied. <see langword="null"/> means "unbounded";
/// a non-null list is an active restriction (empty = deny all).
/// </summary>
public sealed class ToolCeilingResolverTests
{
    [Fact]
    public void ApplyCeiling_NullCeiling_ReturnsCurrentUnchanged()
    {
        IReadOnlyList<string> current = ["read", "write"];

        var result = ToolCeilingResolver.ApplyCeiling(current, null);

        result.Should().BeSameAs(current);
    }

    [Fact]
    public void ApplyCeiling_EmptyCeiling_ReturnsCurrentUnchanged()
    {
        IReadOnlyList<string> current = ["read", "write"];

        var result = ToolCeilingResolver.ApplyCeiling(current, []);

        result.Should().BeSameAs(current);
    }

    [Fact]
    public void ApplyCeiling_UnboundedCurrent_CapsToCeiling()
    {
        // A null current means the skills imposed no restriction (unbounded). A ceiling must still cap
        // the agent — capping "everything" down to the ceiling is a tightening, which is the point.
        var result = ToolCeilingResolver.ApplyCeiling(null, ["read", "search"]);

        result.Should().BeEquivalentTo(["read", "search"]);
    }

    [Fact]
    public void ApplyCeiling_CeilingIsSubset_ReturnsIntersection()
    {
        IReadOnlyList<string> current = ["read", "write", "delete"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["read", "write"]);

        result.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public void ApplyCeiling_CeilingListsToolCurrentDoesNotGrant_NeverWidens()
    {
        // The ceiling names `delete`, but the current allowlist never granted it. The intersection must
        // exclude it: a ceiling requests an upper bound, it does not confer new capability.
        IReadOnlyList<string> current = ["read", "write"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["read", "write", "delete"]);

        result.Should().BeEquivalentTo(["read", "write"]);
        result.Should().NotContain("delete");
    }

    [Fact]
    public void ApplyCeiling_DisjointCeiling_ProducesEmptyDenyAll()
    {
        IReadOnlyList<string> current = ["read", "write"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["deploy", "delete"]);

        // Non-null but empty: an active deny-all, NOT "unbounded".
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyCeiling_ChainedAfterDenyAll_StaysDenyAll_NeverReGrants()
    {
        // Regression for the composition-widening bug: once an intersection has narrowed to empty
        // (deny-all), applying a further ceiling must NOT resurrect any tool. Empty stays empty.
        IReadOnlyList<string> skillUnion = ["read", "write"];

        var afterDisjointCeiling = ToolCeilingResolver.ApplyCeiling(skillUnion, ["deploy"]); // => [] deny-all
        var afterSecondCeiling = ToolCeilingResolver.ApplyCeiling(afterDisjointCeiling, ["read"]);

        afterSecondCeiling.Should().NotBeNull();
        afterSecondCeiling.Should().BeEmpty();
    }

    [Fact]
    public void ApplyCeiling_ResultIsAlwaysSubsetOfCurrent_WhenCurrentBounded()
    {
        IReadOnlyList<string> current = ["read", "write", "search"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["read", "search", "delete", "deploy"]);

        result.Should().BeSubsetOf(current);
    }

    [Fact]
    public void ApplyCeiling_IsCaseInsensitive()
    {
        IReadOnlyList<string> current = ["Read", "Write"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["READ"]);

        result.Should().ContainSingle().Which.Should().Be("Read");
    }

    [Fact]
    public void ApplyCeiling_PreservesCurrentOrder()
    {
        IReadOnlyList<string> current = ["zebra", "alpha", "mike"];

        var result = ToolCeilingResolver.ApplyCeiling(current, ["mike", "zebra", "alpha"]);

        result.Should().ContainInOrder("zebra", "alpha", "mike");
    }

    [Fact]
    public void ApplyCeiling_DeduplicatesCeilingWhenCappingUnboundedCurrent()
    {
        var result = ToolCeilingResolver.ApplyCeiling(null, ["read", "READ", "write"]);

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public void ApplyCeiling_ComposedCeilings_ApplyBothTightenings()
    {
        // Two ceilings applied in sequence (agent ceiling, then an explicit per-call allowlist) must
        // compose to the intersection of all three sets — the strictest wins at every step.
        IReadOnlyList<string> skillUnion = ["read", "write", "search"];

        var afterAgentCeiling = ToolCeilingResolver.ApplyCeiling(skillUnion, ["read", "write"]);
        var afterExplicit = ToolCeilingResolver.ApplyCeiling(afterAgentCeiling, ["read"]);

        afterExplicit.Should().BeEquivalentTo(["read"]);
    }

    [Fact]
    public void ApplyCeiling_NullCurrentAndNoCeiling_StaysUnbounded()
    {
        var result = ToolCeilingResolver.ApplyCeiling(null, null);

        result.Should().BeNull();
    }
}
