using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Services.Agent;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

/// <summary>
/// Tests for <see cref="AgentIdentityResolutionBehavior{TRequest, TResponse}"/> —
/// pass-through paths, fail-loud misconfig, resolver success / failure semantics,
/// and idempotent re-entrant resolution.
/// </summary>
public class AgentIdentityResolutionBehaviorTests
{
    private static IOptionsMonitor<AppConfig> BuildConfig(AgentIdentityConfig identity)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig { Identity = identity }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static AgentIdentityResolutionBehavior<TRequest, TResponse> Build<TRequest, TResponse>(
        IAgentExecutionContext executionContext,
        IOptionsMonitor<AppConfig> config,
        IAgentIdentityResolver? resolver = null)
        where TRequest : notnull
    {
        return new AgentIdentityResolutionBehavior<TRequest, TResponse>(
            executionContext,
            config,
            NullLogger<AgentIdentityResolutionBehavior<TRequest, TResponse>>.Instance,
            resolver);
    }

    [Fact]
    public async Task Handle_NonAgentScopedRequest_PassesThrough()
    {
        var execContext = new AgentExecutionContext();
        var resolver = new Mock<IAgentIdentityResolver>(MockBehavior.Strict);
        var behavior = Build<NonAgentRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver.Object);

        var result = await behavior.Handle(
            new NonAgentRequest(),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        execContext.AgentIdentity.Should().BeNull();
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_IdentityDisabled_PassesThroughWithoutResolverCall()
    {
        var execContext = new AgentExecutionContext();
        var resolver = new Mock<IAgentIdentityResolver>(MockBehavior.Strict);
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = false }),
            resolver.Object);

        var result = await behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        execContext.AgentIdentity.Should().BeNull();
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_IdentityEnabled_NoResolverRegistered_ThrowsInvalidOperation()
    {
        var execContext = new AgentExecutionContext();
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver: null);

        var act = () => behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentIdentityResolver*registered*");
    }

    [Fact]
    public async Task Handle_IdentityEnabled_ResolverSucceeds_StampsIdentityOnContext()
    {
        var resolvedIdentity = new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Success(resolvedIdentity));

        var execContext = new AgentExecutionContext();
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver.Object);

        await behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        execContext.AgentIdentity.Should().Be(resolvedIdentity);
    }

    [Fact]
    public async Task Handle_IdentityEnabled_ResolverFails_ThrowsWithErrorCodes()
    {
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Fail("agent_identity.no_provider_succeeded"));

        var execContext = new AgentExecutionContext();
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver.Object);

        var act = () => behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolution failed*")
            .WithMessage("*agent_identity.no_provider_succeeded*");

        execContext.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public async Task Handle_IdentityEnabled_AlreadyResolved_SkipsResolverCall()
    {
        // Re-entrant request: identity already on context (set by a prior pipeline
        // run within this request scope). Behavior should pass through without
        // calling the resolver again.
        var existingIdentity = new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity
        };
        var execContext = new AgentExecutionContext();
        execContext.SetIdentity(existingIdentity);

        var resolver = new Mock<IAgentIdentityResolver>(MockBehavior.Strict);
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver.Object);

        await behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        execContext.AgentIdentity.Should().Be(existingIdentity);
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_IdentityEnabled_PassesAudienceAndScopesFromConfig()
    {
        CredentialContext? captured = null;
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .Callback<CredentialContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Result<AgentIdentity>.Success(new AgentIdentity
            {
                Id = "planner",
                Kind = AgentIdentityKind.ManagedIdentity
            }));

        var execContext = new AgentExecutionContext();
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig
            {
                Enabled = true,
                DefaultAudience = "api://my-agent",
                DefaultScopes = ["api://my-agent/.default", "https://graph.microsoft.com/.default"]
            }),
            resolver.Object);

        await behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => Task.FromResult("ok"),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Audience.Should().Be("api://my-agent");
        captured.Scopes.Should().HaveCount(2);
        captured.Scopes.Should().Contain("api://my-agent/.default");
        captured.Scopes.Should().Contain("https://graph.microsoft.com/.default");
    }

    [Fact]
    public async Task Handle_IdentityEnabled_HandlerThrows_StillPropagatesException()
    {
        var resolver = new Mock<IAgentIdentityResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CredentialContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AgentIdentity>.Success(new AgentIdentity
            {
                Id = "planner",
                Kind = AgentIdentityKind.ManagedIdentity
            }));

        var execContext = new AgentExecutionContext();
        var behavior = Build<AgentScopedTestRequest, string>(
            execContext,
            BuildConfig(new AgentIdentityConfig { Enabled = true, DefaultAudience = "api://x" }),
            resolver.Object);

        var act = () => behavior.Handle(
            new AgentScopedTestRequest("planner", "conv-1", 1),
            () => throw new InvalidOperationException("handler failed"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("handler failed");

        // Identity was still stamped before the handler ran.
        execContext.AgentIdentity!.Id.Should().Be("planner");
    }

    // Test request types — mirror the AgentContextPropagationBehaviorTests shape.
    public record NonAgentRequest : IRequest<string>;

    public record AgentScopedTestRequest(
        string AgentId,
        string ConversationId,
        int TurnNumber) : IRequest<string>, IAgentScopedRequest;
}
