using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Domain.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// One test per axis of <see cref="GovernanceArmingPolicy"/>. The three surfaces that arm a child scope
/// (direct invocation, sub-plans, delegation) are pinned by their own suites; these prove the shared
/// helper makes each choice the policy names, and that deleting any branch fails a test.
/// </summary>
public sealed class GovernanceArmerTests
{
    private readonly Mock<IAgentExecutionContext> _child = new();
    private readonly Mock<IToolCallAdmissionPipeline> _pipeline = new();
    private readonly Mock<IAgentExecutionContext> _parent = new();

    private IServiceProvider ChildServices(bool withPipeline = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_child.Object);
        if (withPipeline)
            services.AddSingleton(_pipeline.Object);
        return services.BuildServiceProvider();
    }

    private static GovernanceArmingPolicy Policy(
        bool mint = false,
        CallOnceScopeSource callOnce = CallOnceScopeSource.InheritAsIs,
        bool inheritTurn = false,
        bool identity = false,
        bool pipeline = false) => new()
    {
        MintConversationId = mint,
        CallOnceScope = callOnce,
        InheritTurnNumber = inheritTurn,
        PropagateWorkloadIdentity = identity,
        ResolvePipeline = pipeline,
    };

    // ---- conversation id ------------------------------------------------------------------------

    [Fact]
    public void Arm_MintPolicy_GivesEachArmingItsOwnConversationId_IgnoringTheParent()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("parent-conv");
        var ids = new List<string>();
        _child
            .Setup(c => c.Initialize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Callback<string, string, int, string?>((_, conv, _, _) => ids.Add(conv));

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(mint: true), _parent.Object);
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(mint: true), _parent.Object);

        ids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        ids.Should().NotContain("parent-conv");
    }

    [Fact]
    public void Arm_InheritPolicy_UsesTheParentsConversationId()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("parent-conv");

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(), _parent.Object, "fallback");

        _child.Verify(c => c.Initialize("agent", "parent-conv", 1, null), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Arm_InheritPolicy_ParentConversationIdNullOrEmpty_UsesTheFallback(string? parentId)
    {
        _parent.SetupGet(p => p.ConversationId).Returns(parentId);

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(), _parent.Object, "fallback");

        _child.Verify(c => c.Initialize("agent", "fallback", 1, null), Times.Once);
    }

    [Fact]
    public void Arm_InheritPolicy_NoParent_UsesTheFallback()
    {
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(), parent: null, fallbackScopeId: "fallback");

        _child.Verify(c => c.Initialize("agent", "fallback", 1, null), Times.Once);
    }

    [Fact]
    public void Arm_InheritPolicyNeedingAFallback_WithoutOne_Throws()
    {
        // Nothing to inherit and nothing to fall back to would stamp a null/empty id onto the child.
        var act = () => GovernanceArmer.Arm(ChildServices(), "agent", Policy(), parent: null);

        act.Should().Throw<ArgumentException>().WithParameterName("fallbackScopeId");
    }

    // ---- call-once scope ------------------------------------------------------------------------

    [Fact]
    public void Arm_OmitCallOnce_LeavesItNull_EvenWhenTheParentHasOne()
    {
        _parent.SetupGet(p => p.CallOnceScopeId).Returns("parent-scope");

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(mint: true, callOnce: CallOnceScopeSource.Omit), _parent.Object);

        _child.Verify(c => c.Initialize("agent", It.IsAny<string>(), 1, null), Times.Once);
    }

    [Fact]
    public void Arm_InheritAsIs_PassesTheParentScopeThrough_AndKeepsNullNull()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.CallOnceScopeId).Returns("parent-scope");
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(callOnce: CallOnceScopeSource.InheritAsIs), _parent.Object, "fb");
        _child.Verify(c => c.Initialize("agent", "conv", 1, "parent-scope"), Times.Once);

        _child.Invocations.Clear();
        _parent.SetupGet(p => p.CallOnceScopeId).Returns((string?)null);
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(callOnce: CallOnceScopeSource.InheritAsIs), _parent.Object, "fb");
        _child.Verify(c => c.Initialize("agent", "conv", 1, null), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Arm_InheritOrFallback_ParentScopeNullOrEmpty_UsesTheFallback(string? parentScope)
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.CallOnceScopeId).Returns(parentScope);

        GovernanceArmer.Arm(
            ChildServices(), "agent", Policy(callOnce: CallOnceScopeSource.InheritOrFallback), _parent.Object, "fallback");

        _child.Verify(c => c.Initialize("agent", "conv", 1, "fallback"), Times.Once);
    }

    [Fact]
    public void Arm_InheritOrFallback_ParentHasAScope_UsesIt()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.CallOnceScopeId).Returns("parent-scope");

        GovernanceArmer.Arm(
            ChildServices(), "agent", Policy(callOnce: CallOnceScopeSource.InheritOrFallback), _parent.Object, "fallback");

        _child.Verify(c => c.Initialize("agent", "conv", 1, "parent-scope"), Times.Once);
    }

    // ---- turn number ----------------------------------------------------------------------------

    [Fact]
    public void Arm_InheritTurn_UsesTheParentsTurn_AndOneWhenThereIsNone()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.TurnNumber).Returns(5);
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(inheritTurn: true), _parent.Object, "fb");
        _child.Verify(c => c.Initialize("agent", "conv", 5, null), Times.Once);

        _child.Invocations.Clear();
        _parent.SetupGet(p => p.TurnNumber).Returns((int?)null);
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(inheritTurn: true), _parent.Object, "fb");
        _child.Verify(c => c.Initialize("agent", "conv", 1, null), Times.Once);
    }

    [Fact]
    public void Arm_FixedTurn_StartsAtOne_WhateverTheParentIsOn()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.TurnNumber).Returns(5);

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(inheritTurn: false), _parent.Object, "fb");

        _child.Verify(c => c.Initialize("agent", "conv", 1, null), Times.Once);
    }

    // ---- workload identity ----------------------------------------------------------------------

    [Fact]
    public void Arm_PropagateIdentity_StampsTheParentsIdentityOntoTheChild()
    {
        var identity = new AgentIdentity { Id = "caller-principal", Kind = AgentIdentityKind.Development };
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.AgentIdentity).Returns(identity);

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(identity: true), _parent.Object, "fb");

        _child.Verify(c => c.SetIdentity(identity), Times.Once);
    }

    [Fact]
    public void Arm_PropagateIdentity_ParentHasNone_NeverInventsOne()
    {
        _parent.SetupGet(p => p.ConversationId).Returns("conv");

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(identity: true), _parent.Object, "fb");
        GovernanceArmer.Arm(ChildServices(), "agent", Policy(identity: true), parent: null, fallbackScopeId: "fb");

        _child.Verify(c => c.SetIdentity(It.IsAny<AgentIdentity>()), Times.Never);
    }

    [Fact]
    public void Arm_IdentityNotPropagatedByPolicy_LeavesTheChildsIdentityAlone()
    {
        var identity = new AgentIdentity { Id = "caller-principal", Kind = AgentIdentityKind.Development };
        _parent.SetupGet(p => p.ConversationId).Returns("conv");
        _parent.SetupGet(p => p.AgentIdentity).Returns(identity);

        GovernanceArmer.Arm(ChildServices(), "agent", Policy(identity: false), _parent.Object, "fb");

        _child.Verify(c => c.SetIdentity(It.IsAny<AgentIdentity>()), Times.Never);
    }

    // ---- pipeline -------------------------------------------------------------------------------

    [Fact]
    public void Arm_ResolvePipeline_ResetsTheChildsPipelineExactlyOnce()
    {
        var armed = GovernanceArmer.Arm(
            ChildServices(), "agent", Policy(mint: true, callOnce: CallOnceScopeSource.Omit, pipeline: true));

        armed.Pipeline.Should().BeSameAs(_pipeline.Object);
        _pipeline.Verify(p => p.Reset(), Times.Once);
    }

    [Fact]
    public void Arm_NoPipelineByPolicy_DoesNotTouchOneAndRefusesToHandOneOut()
    {
        // The container has no pipeline registered, so resolving one would throw — proving it is not
        // resolved at all, not merely ignored.
        var armed = GovernanceArmer.Arm(
            ChildServices(withPipeline: false), "agent", Policy(), parent: null, fallbackScopeId: "fb");

        _pipeline.Verify(p => p.Reset(), Times.Never);
        var pipeline = () => armed.Pipeline;
        pipeline.Should().Throw<InvalidOperationException>();
        var activate = () => armed.Activate();
        activate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Arm_ResolvePipeline_NotRegistered_Throws()
    {
        // A broken composition must fail loudly rather than run the path silently unguarded.
        var act = () => GovernanceArmer.Arm(
            ChildServices(withPipeline: false), "agent", Policy(mint: true, pipeline: true));

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- activation -----------------------------------------------------------------------------

    [Fact]
    public void Activate_PublishesTheChildsPipelineAmbiently_AndRestoresTheEnclosingOne()
    {
        var outer = Mock.Of<IToolCallAdmissionPipeline>();
        var armed = GovernanceArmer.Arm(
            ChildServices(), "agent", Policy(mint: true, callOnce: CallOnceScopeSource.Omit, pipeline: true));

        using (ToolAdmissionAccessor.Begin(outer))
        {
            using (armed.Activate())
                ToolAdmissionAccessor.Current.Should().BeSameAs(_pipeline.Object);

            ToolAdmissionAccessor.Current.Should().BeSameAs(outer,
                "disposing must restore the enclosing turn's chain, never disarm it");
        }
    }

    [Fact]
    public void Arm_UnknownCallOnceSource_ThrowsBeforeTheContextIsInitialised()
    {
        // A CallOnceScopeSource member added without a case must fail loudly, and must do so before
        // anything is stamped onto the child — a half-initialised context cannot be re-initialised.
        var act = () => GovernanceArmer.Arm(
            ChildServices(), "agent", Policy(mint: true, callOnce: (CallOnceScopeSource)99));

        act.Should().Throw<ArgumentOutOfRangeException>();
        _child.Verify(
            c => c.Initialize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>()),
            Times.Never);
    }

    // ---- arguments ------------------------------------------------------------------------------

    [Fact]
    public void Arm_BlankAgentId_Throws()
    {
        var act = () => GovernanceArmer.Arm(ChildServices(), "", Policy(mint: true));

        act.Should().Throw<ArgumentException>();
    }
}
