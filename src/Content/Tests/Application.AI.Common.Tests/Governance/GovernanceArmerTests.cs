using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Domain.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// One test (or theory) per axis of <see cref="GovernanceArmingPolicy"/>, against the real
/// <see cref="AgentExecutionContext"/> so what is asserted is the state the child ends up in — including
/// the real Initialize-then-SetIdentity interaction — rather than the shape of a call. The surfaces that
/// arm a child scope (direct invocation, sub-plans, delegation) are pinned by their own suites; these
/// prove the shared helper makes each choice the policy names.
/// </summary>
public sealed class GovernanceArmerTests
{
    private static readonly AgentIdentity Identity =
        new() { Id = "caller-principal", Kind = AgentIdentityKind.Development };

    private readonly Mock<IToolCallAdmissionPipeline> _pipeline = new();

    /// <summary>A fresh child scope's provider, plus the child context it will hand out.</summary>
    private (IServiceProvider Services, AgentExecutionContext Child) NewChild(bool withPipeline = true)
    {
        var child = new AgentExecutionContext();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentExecutionContext>(child);
        if (withPipeline)
            services.AddSingleton(_pipeline.Object);
        return (services.BuildServiceProvider(), child);
    }

    /// <summary>Arms a fresh child and returns it, so each case asserts on the state it ended up in.</summary>
    private AgentExecutionContext Arm(
        GovernanceArmingPolicy policy, IAgentExecutionContext? parent = null, string? fallback = "fallback")
    {
        var (services, child) = NewChild();
        GovernanceArmer.Arm(services, "agent", policy, parent, fallback);
        return child;
    }

    /// <summary>
    /// A parent turn's context. A <see langword="null"/> <paramref name="conversation"/> leaves it
    /// uninitialised, i.e. with no conversation id, turn number or call-once scope at all.
    /// </summary>
    private static AgentExecutionContext Parent(
        string? conversation = "parent-conv", string? callOnce = null, int turn = 1, AgentIdentity? identity = null)
    {
        var parent = new AgentExecutionContext();
        if (conversation is not null)
            parent.Initialize("parent-agent", conversation, turn, callOnce);
        if (identity is not null)
            parent.SetIdentity(identity);
        return parent;
    }

    // ---- conversation id ------------------------------------------------------------------------

    [Fact]
    public void Arm_MintPolicy_GivesEachArmingItsOwnConversationId_IgnoringTheParent()
    {
        var parent = Parent();

        var first = Arm(GovernanceArmingPolicy.DirectInvocation, parent);
        var second = Arm(GovernanceArmingPolicy.DirectInvocation, parent);

        first.ConversationId.Should().NotBeNullOrWhiteSpace().And.NotBe("parent-conv");
        second.ConversationId.Should().NotBe(first.ConversationId);
    }

    [Theory]
    [InlineData("parent-conv", "parent-conv")]
    [InlineData(null, "fallback")]
    [InlineData("", "fallback")]
    public void Arm_InheritPolicy_UsesTheParentsConversationId_ElseTheFallback(
        string? parentConversation, string expected)
    {
        // Null is an uninitialised parent; the empty string is an initialised one that supplies nothing.
        var child = Arm(GovernanceArmingPolicy.SubPlan, Parent(parentConversation));

        child.ConversationId.Should().Be(expected);
    }

    [Fact]
    public void Arm_InheritPolicy_NoParent_UsesTheFallback()
    {
        Arm(GovernanceArmingPolicy.SubPlan, parent: null).ConversationId.Should().Be("fallback");
    }

    [Fact]
    public void Arm_InheritPolicyNeedingAFallback_WithoutOne_Throws()
    {
        // Nothing to inherit and nothing to fall back to would stamp a null/empty id onto the child.
        var (services, _) = NewChild();

        var act = () => GovernanceArmer.Arm(services, "agent", GovernanceArmingPolicy.SubPlan, parent: null);

        act.Should().Throw<ArgumentException>().WithParameterName("fallbackScopeId");
    }

    // ---- call-once scope ------------------------------------------------------------------------

    [Theory]
    [InlineData(CallOnceScopeSource.Omit, "parent-scope", null)]
    [InlineData(CallOnceScopeSource.InheritAsIs, "parent-scope", "parent-scope")]
    [InlineData(CallOnceScopeSource.InheritAsIs, null, null)]
    [InlineData(CallOnceScopeSource.InheritOrFallback, "parent-scope", "parent-scope")]
    [InlineData(CallOnceScopeSource.InheritOrFallback, null, "fallback")]
    [InlineData(CallOnceScopeSource.InheritOrFallback, "", "fallback")]
    public void Arm_CallOnceScope_FollowsTheSource(
        CallOnceScopeSource source, string? parentScope, string? expected)
    {
        var policy = GovernanceArmingPolicy.SubPlan with { CallOnceScope = source };

        var child = Arm(policy, Parent(callOnce: parentScope));

        child.CallOnceScopeId.Should().Be(expected);
    }

    [Fact]
    public void Arm_UnknownCallOnceSource_ThrowsBeforeTheContextIsInitialised()
    {
        // A CallOnceScopeSource member added without a case must fail loudly, and must do so before
        // anything is stamped onto the child — a half-initialised context cannot be re-initialised.
        var (services, child) = NewChild();
        var policy = GovernanceArmingPolicy.SubPlan with { CallOnceScope = (CallOnceScopeSource)99 };

        var act = () => GovernanceArmer.Arm(services, "agent", policy, Parent(), "fallback");

        act.Should().Throw<ArgumentOutOfRangeException>();
        child.AgentId.Should().BeNull("nothing was initialised");
    }

    // ---- turn number ----------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "parent-conv", 5, 5)]
    [InlineData(true, null, 0, 1)]
    [InlineData(false, "parent-conv", 5, 1)]
    public void Arm_TurnNumber_IsTheParentsOrOne(bool inherit, string? parentConversation, int parentTurn, int expected)
    {
        var policy = GovernanceArmingPolicy.SubPlan with { InheritTurnNumber = inherit };

        var child = Arm(policy, Parent(parentConversation, turn: parentTurn));

        child.TurnNumber.Should().Be(expected);
    }

    // ---- workload identity ----------------------------------------------------------------------

    [Fact]
    public void Arm_ParentHasAWorkloadIdentity_ItTravelsToTheChild_AfterTheContextIsInitialised()
    {
        // Asserted on a real context: SetIdentity after Initialize is the contract, and a child that
        // lost its identity would be authorized as the host's default.
        var child = Arm(GovernanceArmingPolicy.SubPlan, Parent(identity: Identity));

        child.AgentId.Should().Be("agent");
        child.AgentIdentity.Should().Be(Identity);
    }

    [Fact]
    public void Arm_ParentHasNoWorkloadIdentity_NeverInventsOne()
    {
        Arm(GovernanceArmingPolicy.SubPlan, Parent()).AgentIdentity.Should().BeNull();
        Arm(GovernanceArmingPolicy.SubPlan, parent: null).AgentIdentity.Should().BeNull();
    }

    // ---- admission pipeline ---------------------------------------------------------------------

    [Fact]
    public void ArmWithAdmission_ResetsTheChildsPipelineExactlyOnce_AndReturnsIt()
    {
        var (services, child) = NewChild();

        var pipeline = GovernanceArmer.ArmWithAdmission(services, "agent", GovernanceArmingPolicy.DirectInvocation);

        pipeline.Should().BeSameAs(_pipeline.Object);
        _pipeline.Verify(p => p.Reset(), Times.Once);
        child.AgentId.Should().Be("agent", "the context is armed before the pipeline is resolved");
    }

    [Fact]
    public void ArmWithAdmission_PipelineNotRegistered_Throws()
    {
        // A broken composition must fail loudly rather than run the path silently unguarded.
        var (services, _) = NewChild(withPipeline: false);

        var act = () => GovernanceArmer.ArmWithAdmission(services, "agent", GovernanceArmingPolicy.DirectInvocation);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Arm_WithoutAdmission_NeverTouchesThePipeline()
    {
        // The container has no pipeline registered, so resolving one would throw — proving Arm does not
        // resolve it at all, which is what sub-plans (whose steps call their own pipeline) rely on.
        var (services, child) = NewChild(withPipeline: false);

        GovernanceArmer.Arm(services, "agent", GovernanceArmingPolicy.SubPlan, Parent(), "fallback");

        child.AgentId.Should().Be("agent");
        _pipeline.Verify(p => p.Reset(), Times.Never);
    }

    // ---- arguments ------------------------------------------------------------------------------

    [Fact]
    public void Arm_BlankAgentId_Throws()
    {
        var (services, _) = NewChild();

        var act = () => GovernanceArmer.Arm(services, "", GovernanceArmingPolicy.DirectInvocation);

        act.Should().Throw<ArgumentException>();
    }
}
