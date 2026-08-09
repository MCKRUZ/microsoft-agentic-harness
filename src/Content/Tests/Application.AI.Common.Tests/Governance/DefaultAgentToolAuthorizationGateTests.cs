using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Services.Governance;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Tests for <see cref="DefaultAgentToolAuthorizationGate"/> — the seam that decides whether
/// per-agent tool RBAC applies at all, and how the executing identity is obtained when it does.
/// </summary>
/// <remarks>
/// <para>
/// The policy decision itself belongs to <c>EntraAgentIdentityValidator</c> and is tested there.
/// What is tested here is everything that determines whether that decision is ever reached: the
/// off switch, and the four ways an enabled gate can fail to establish who is asking. Each of
/// those must deny, because a permissive answer to "I cannot tell which agent this is" is the
/// defect this whole type exists to close.
/// </para>
/// <para>
/// The identity-acquisition fallback carries most of the weight. The execution context holds an
/// identity only on the agent-turn path — the plan engine's step executors and the Execution API's
/// direct invoker each open a fresh DI scope whose context is blank — so a gate that read only the
/// context would enforce on one of four execution paths and be bypassable by issuing the same call
/// from a plan step.
/// </para>
/// </remarks>
public sealed class DefaultAgentToolAuthorizationGateTests
{
    private const string Tool = "file_system";

    private static readonly AgentIdentity StampedIdentity =
        new() { Id = "agent-from-context", Kind = AgentIdentityKind.ManagedIdentity };

    private static readonly AgentIdentity ResolvedIdentity =
        new() { Id = "agent-from-resolver", Kind = AgentIdentityKind.ManagedIdentity };

    [Fact]
    public async Task EvaluateAsync_FeatureOff_AdmitsWithoutConsultingAnything()
    {
        var validator = new Mock<IAgentIdentityValidator>(MockBehavior.Strict);
        var resolver = new Mock<IAgentIdentityResolver>(MockBehavior.Strict);
        var gate = Build(enabled: false, validator: validator.Object, resolver: resolver.Object);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeTrue(
            "the harness shipped without per-agent RBAC for every release before this switch existed, "
            + "so an unconfigured host must behave exactly as it did");

        // Strict mocks: any consultation at all would have thrown. The off state is not "ask and
        // ignore the answer", it is "do not ask" — which is what keeps an unconfigured host from
        // paying for a token acquisition on every tool call.
        validator.VerifyNoOtherCalls();
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_IdentityOnContext_DelegatesToTheValidator()
    {
        var validator = Validator(StampedIdentity, Tool, allowed: true);
        var gate = Build(enabled: true, contextIdentity: StampedIdentity, validator: validator.Object);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeTrue();
        validator.Verify(v => v.CanInvoke(StampedIdentity, Tool), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_ValidatorRefuses_Denies()
    {
        var validator = Validator(StampedIdentity, Tool, allowed: false);
        var gate = Build(enabled: true, contextIdentity: StampedIdentity, validator: validator.Object);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeFalse();
        verdict.DeniedMessage.Should().NotBeNullOrWhiteSpace(
            "a refusal with no text reaches a model as an empty successful result, which reads as the "
            + "tool having run and returned nothing");
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_ContextHasNoIdentity_ResolvesOneAndAuthorizesIt()
    {
        // The plan-engine and Execution API paths. Their scopes carry no identity, and the workload
        // identity is a process-level principal rather than a per-request value, so resolving it here
        // yields the same principal the agent turn would have carried.
        var validator = Validator(ResolvedIdentity, Tool, allowed: true);
        var resolver = Resolver(Result<AgentIdentity>.Success(ResolvedIdentity));
        var gate = Build(
            enabled: true, contextIdentity: null, validator: validator.Object, resolver: resolver.Object);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeTrue(
            "a gate that enforced only where an identity was pre-stamped would be bypassable by "
            + "issuing the same call from a plan step");
        validator.Verify(v => v.CanInvoke(ResolvedIdentity, Tool), Times.Once);
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_ResolvedIdentityIsReusedAcrossCallsInTheSameScope()
    {
        var validator = Validator(ResolvedIdentity, Tool, allowed: true);
        var resolver = Resolver(Result<AgentIdentity>.Success(ResolvedIdentity));
        var gate = Build(
            enabled: true, contextIdentity: null, validator: validator.Object, resolver: resolver.Object);

        await gate.EvaluateAsync(Tool, CancellationToken.None);
        await gate.EvaluateAsync("calculation_engine", CancellationToken.None);
        await gate.EvaluateAsync("shell_exec", CancellationToken.None);

        resolver.Verify(
            r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the gate is scoped, so a plan step issuing many tool calls must pay for at most one "
            + "credential acquisition rather than one per call");
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_ResolutionFails_Denies()
    {
        var validator = new Mock<IAgentIdentityValidator>(MockBehavior.Strict);
        var resolver = Resolver(Result<AgentIdentity>.Fail("agent_identity.no_provider_succeeded"));
        var gate = Build(
            enabled: true, contextIdentity: null, validator: validator.Object, resolver: resolver.Object);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeFalse(
            "failing to establish who is asking is the one case where a permissive answer is "
            + "indefensible");
        validator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_FailedResolutionIsNotRetriedForEveryCall()
    {
        var resolver = Resolver(Result<AgentIdentity>.Fail("agent_identity.no_provider_succeeded"));
        var gate = Build(
            enabled: true,
            contextIdentity: null,
            validator: Mock.Of<IAgentIdentityValidator>(),
            resolver: resolver.Object);

        await gate.EvaluateAsync(Tool, CancellationToken.None);
        await gate.EvaluateAsync(Tool, CancellationToken.None);

        resolver.Verify(
            r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a host whose credentials are misconfigured must not hammer the credential endpoint once "
            + "per denied tool call");
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_NoValidatorRegistered_Denies()
    {
        var gate = Build(enabled: true, contextIdentity: StampedIdentity, validator: null);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeFalse(
            "an enabled feature with no policy oracle cannot answer the question, and unanswerable "
            + "must not mean permitted");
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_NoResolverRegisteredAndNoContextIdentity_Denies()
    {
        var gate = Build(
            enabled: true,
            contextIdentity: null,
            validator: Mock.Of<IAgentIdentityValidator>(),
            resolver: null);

        var verdict = await gate.EvaluateAsync(Tool, CancellationToken.None);

        verdict.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_ContextIdentityWinsOverTheResolver()
    {
        // On the agent-turn path the resolution behaviour has already paid for acquisition, and
        // re-resolving would risk authorizing against a different principal than the rest of the turn
        // is using.
        var validator = Validator(StampedIdentity, Tool, allowed: true);
        var resolver = Resolver(Result<AgentIdentity>.Success(ResolvedIdentity));
        var gate = Build(
            enabled: true,
            contextIdentity: StampedIdentity,
            validator: validator.Object,
            resolver: resolver.Object);

        await gate.EvaluateAsync(Tool, CancellationToken.None);

        validator.Verify(v => v.CanInvoke(StampedIdentity, Tool), Times.Once);
        resolver.Verify(
            r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_FeatureOn_CancellationPropagatesRatherThanBecomingADenial()
    {
        // An abandoned call is not a policy decision. Swallowing the cancellation into a deny would
        // record a governance refusal that never happened.
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var gate = Build(
            enabled: true,
            contextIdentity: null,
            validator: Mock.Of<IAgentIdentityValidator>(),
            resolver: resolver.Object);

        var act = async () => await gate.EvaluateAsync(Tool, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static DefaultAgentToolAuthorizationGate Build(
        bool enabled,
        AgentIdentity? contextIdentity = null,
        IAgentIdentityValidator? validator = null,
        IAgentIdentityResolver? resolver = null)
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    Enabled = enabled,
                    ToolAuthorization = new ToolAuthorizationConfig { Enabled = enabled }
                }
            }
        };

        var context = new Mock<IAgentExecutionContext>();
        context.SetupGet(c => c.AgentIdentity).Returns(contextIdentity);

        return new DefaultAgentToolAuthorizationGate(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            context.Object,
            NullLogger<DefaultAgentToolAuthorizationGate>.Instance,
            validator,
            resolver);
    }

    private static Mock<IAgentIdentityValidator> Validator(AgentIdentity identity, string toolKey, bool allowed)
    {
        var validator = new Mock<IAgentIdentityValidator>();
        validator.Setup(v => v.CanInvoke(identity, toolKey)).Returns(allowed);
        return validator;
    }

    private static Mock<IAgentIdentityResolver> Resolver(Result<AgentIdentity> result)
    {
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return resolver;
    }
}
