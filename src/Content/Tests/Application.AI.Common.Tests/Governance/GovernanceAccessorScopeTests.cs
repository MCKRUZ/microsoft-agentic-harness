using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for the restoring <c>Begin</c> scope on <see cref="ToolAdmissionAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The four gates were previously published through four separate accessors, each armed by assigning
/// <c>Current</c> and nulling it in a <c>finally</c>. That is not equivalent to restoring, and the
/// difference only shows under nesting — which is exactly where it bites: an inner governed call
/// finishing inside an outer one's window nulls the ambient value, and the outer call runs
/// <strong>ungoverned</strong> for the rest of its life. There is no error, no log line, and the outer
/// call still returns a result.
/// </para>
/// <para>
/// There is now one accessor and it has no public setter, so the assign-and-null idiom is not
/// expressible. These tests pin the restore semantics that replaced it.
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
    public void Beginning_an_admission_scope_publishes_it()
    {
        var pipeline = Pipeline();

        using var scope = ToolAdmissionAccessor.Begin(pipeline);

        ToolAdmissionAccessor.Current.Should().BeSameAs(pipeline);
    }

    [Fact]
    public void Disposing_an_admission_scope_restores_the_outer_chain()
    {
        // The property the old set-and-null idiom did not have. Without it the outer call continues
        // with nothing ambient, which the governed tool wrapper reads as "not a governed flow" and
        // passes straight through.
        var outer = Pipeline();
        var inner = Pipeline();

        using var outerScope = ToolAdmissionAccessor.Begin(outer);
        using (ToolAdmissionAccessor.Begin(inner))
        {
            ToolAdmissionAccessor.Current.Should().BeSameAs(inner);
        }

        ToolAdmissionAccessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void Disposing_the_outermost_admission_scope_leaves_nothing_armed()
    {
        var pipeline = Pipeline();

        using (ToolAdmissionAccessor.Begin(pipeline))
        {
        }

        ToolAdmissionAccessor.Current.Should().BeNull();
    }

    [Fact]
    public void Disposing_an_admission_scope_twice_does_not_re_restore()
    {
        // A double dispose must not overwrite whatever was armed in between, or a stray `using` in a
        // refactor silently disarms a live flow.
        var outer = Pipeline();
        var scope = ToolAdmissionAccessor.Begin(Pipeline());
        scope.Dispose();

        using var reArmed = ToolAdmissionAccessor.Begin(outer);
        scope.Dispose();

        ToolAdmissionAccessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void A_null_chain_is_refused()
    {
        // Publishing null would read downstream as "not a governed flow" — the fail-open state,
        // arrived at by an arming call. Better to throw at the call site.
        var act = () => ToolAdmissionAccessor.Begin(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static IToolCallAdmissionPipeline Pipeline() => new Mock<IToolCallAdmissionPipeline>().Object;
}
