using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for the restoring <c>Begin</c> scopes on <see cref="ToolGovernanceAccessor"/> and
/// <see cref="ClassificationGateAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// These accessors were previously armed by assigning <c>Current</c> and nulling it in a
/// <c>finally</c>. That is not equivalent to restoring, and the difference only shows under nesting —
/// which is exactly where it bites: an inner governed call finishing inside an outer one's window
/// nulls the ambient governor, and the outer call runs <strong>ungoverned</strong> for the rest of its
/// life. There is no error, no log line, and the outer call still returns a result.
/// </para>
/// <para>
/// Everything here is asserted within a single synchronous frame on purpose. An <c>AsyncLocal</c>
/// written inside an awaited method is invisible to the awaiting caller, so an assertion made after an
/// <c>await</c> would pass no matter what the callee did — the vacuous shape this suite exists to
/// avoid.
/// </para>
/// </remarks>
public sealed class GovernanceAccessorScopeTests
{
    [Fact]
    public void Beginning_a_governor_scope_publishes_it()
    {
        var governor = Governor();

        using var scope = ToolGovernanceAccessor.Begin(governor);

        ToolGovernanceAccessor.Current.Should().BeSameAs(governor);
    }

    [Fact]
    public void Disposing_a_governor_scope_restores_the_outer_governor()
    {
        // The property the old set-and-null idiom did not have. Without it the outer call continues
        // with no ambient governor, which the ToolInvocationGovernor reads as "not a governed flow".
        var outer = Governor();
        var inner = Governor();

        using var outerScope = ToolGovernanceAccessor.Begin(outer);
        using (ToolGovernanceAccessor.Begin(inner))
        {
            ToolGovernanceAccessor.Current.Should().BeSameAs(inner);
        }

        ToolGovernanceAccessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void Disposing_the_outermost_governor_scope_leaves_nothing_armed()
    {
        var governor = Governor();

        using (ToolGovernanceAccessor.Begin(governor))
        {
        }

        ToolGovernanceAccessor.Current.Should().BeNull();
    }

    [Fact]
    public void Disposing_a_governor_scope_twice_does_not_re_restore()
    {
        // A double dispose must not overwrite whatever was armed in between, or a stray `using` in a
        // refactor silently disarms a live flow.
        var outer = Governor();
        var scope = ToolGovernanceAccessor.Begin(Governor());
        scope.Dispose();

        using var reArmed = ToolGovernanceAccessor.Begin(outer);
        scope.Dispose();

        ToolGovernanceAccessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void A_null_governor_is_refused()
    {
        // Publishing null would read downstream as "not a governed flow" — the fail-open state, arrived
        // at by an arming call. Better to throw at the call site.
        var act = () => ToolGovernanceAccessor.Begin(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Disposing_a_gate_scope_restores_the_outer_gate()
    {
        var outer = Gate();
        var inner = Gate();

        using var outerScope = ClassificationGateAccessor.Begin(outer);
        using (ClassificationGateAccessor.Begin(inner))
        {
            ClassificationGateAccessor.Current.Should().BeSameAs(inner);
        }

        ClassificationGateAccessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void A_null_gate_is_accepted_and_still_restores()
    {
        // Unlike the governor, a null gate is a legitimate state — a host may register none — so the
        // call site should not need a special case for it. What must still hold is the restore.
        var outer = Gate();

        using var outerScope = ClassificationGateAccessor.Begin(outer);
        using (ClassificationGateAccessor.Begin(null))
        {
            ClassificationGateAccessor.Current.Should().BeNull();
        }

        ClassificationGateAccessor.Current.Should().BeSameAs(outer);
    }

    private static IToolInvocationGovernor Governor()
    {
        var mock = new Mock<IToolInvocationGovernor>();
        mock.Setup(g => g.GetTrace()).Returns(GovernanceTrace.Empty);
        return mock.Object;
    }

    private static IToolClassificationGate Gate() => new Mock<IToolClassificationGate>().Object;
}
