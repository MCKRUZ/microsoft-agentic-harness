using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.MediatRBehaviors;
using Application.AI.Common.Services.Agent;
using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Identity;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// End-to-end integration tests for the agent-identity subsystem. Wires a real
/// MediatR pipeline with the actual <see cref="AgentIdentityResolutionBehavior{TRequest, TResponse}"/>,
/// the actual <see cref="EntraAgentIdResolver"/>, and the actual
/// <see cref="DevelopmentAgentCredentialProvider"/> (no mocks for the components
/// under test) and sends an <see cref="IAgentScopedRequest"/> through it. These
/// catch the "we refactored the pipeline and forgot to stamp identity" regression
/// class.
/// </summary>
/// <remarks>
/// Each test builds an isolated <see cref="ServiceProvider"/> + scope so the
/// <see cref="IAgentExecutionContext"/> is fresh per test — scope-leak detection
/// would throw across tests sharing a context.
/// </remarks>
public sealed class AgentIdentityIntegrationTests
{
    private static ServiceProvider BuildPipeline(
        AgentIdentityConfig identityConfig,
        string envName = "Development",
        bool registerKnowledgeScope = false)
    {
        var services = new ServiceCollection();

        // Logging
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        // Config
        var appConfig = new AppConfig
        {
            AI = new AIConfig { Identity = identityConfig }
        };
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig));

        // Host environment — drives the Development credential provider gate
        services.AddSingleton<IHostEnvironment>(
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == envName));

        // Scoped agent execution context — the real implementation that the behavior writes to
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // Real credential provider + resolver + validator (the subjects under test)
        services.AddSingleton<IAgentCredentialProvider, DevelopmentAgentCredentialProvider>();
        services.AddSingleton<IAgentIdentityResolver, EntraAgentIdResolver>();
        services.AddSingleton<IAgentIdentityValidator, EntraAgentIdentityValidator>();

        // Optionally register a fake human-caller scope alongside identity so the
        // two-ambient coexistence is exercised.
        if (registerKnowledgeScope)
        {
            services.AddScoped<IKnowledgeScope>(_ => new FakeKnowledgeScope
            {
                UserId = "alice",
                TenantId = "tenant-contoso"
            });
        }

        // MediatR — register the pipeline behaviors and the test handler
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblyContaining<AgentIdentityIntegrationTests>());

        services.AddTransient(
            typeof(IPipelineBehavior<,>),
            typeof(AgentContextPropagationBehavior<,>));
        services.AddTransient(
            typeof(IPipelineBehavior<,>),
            typeof(AgentIdentityResolutionBehavior<,>));

        return services.BuildServiceProvider();
    }

    private static AgentIdentityConfig DevelopmentConfig(string agentId = "dev-agent") => new()
    {
        Enabled = true,
        DefaultAudience = "api://test-agent",
        Development = new DevelopmentProviderConfig
        {
            AgentId = agentId,
            TenantId = "dev-tenant",
            ObjectId = "dev-oid-1"
        }
    };

    [Fact]
    public async Task Pipeline_IdentityDisabled_PassesThroughWithoutStamping()
    {
        var config = new AgentIdentityConfig { Enabled = false };
        await using var provider = BuildPipeline(config);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        var result = await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        result.Ok.Should().BeTrue();
        execContext.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_IdentityEnabled_ResolvesAndStampsDevelopmentIdentity()
    {
        await using var provider = BuildPipeline(DevelopmentConfig());
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        var result = await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        result.Ok.Should().BeTrue();
        execContext.AgentIdentity.Should().NotBeNull();
        execContext.AgentIdentity!.Id.Should().Be("dev-agent");
        execContext.AgentIdentity.Kind.Should().Be(AgentIdentityKind.Development);
        execContext.AgentIdentity.TenantId.Should().Be("dev-tenant");
        execContext.AgentIdentity.ObjectId.Should().Be("dev-oid-1");
    }

    [Fact]
    public async Task Pipeline_NonAgentScopedRequest_LeavesIdentityNull()
    {
        await using var provider = BuildPipeline(DevelopmentConfig());
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        // NonAgentRequest does NOT implement IAgentScopedRequest → behavior passes through
        var result = await mediator.Send(new NonAgentRequest());

        result.Ok.Should().BeTrue();
        execContext.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_TwoNestedAgentScopedRequests_IdentityRemainsStable()
    {
        // Re-entrant: outer request resolves identity; inner request sees the same identity
        // already on the context, AgentIdentityResolutionBehavior short-circuits, and the
        // pipeline does not call the resolver again.
        await using var provider = BuildPipeline(DevelopmentConfig());
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));
        var firstStamp = execContext.AgentIdentity;

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 2));
        var secondStamp = execContext.AgentIdentity;

        firstStamp.Should().NotBeNull();
        secondStamp.Should().BeSameAs(firstStamp);
    }

    [Fact]
    public async Task Pipeline_TwoAgentScopedRequests_DifferentAgentInSameScope_ThrowsScopeConflict()
    {
        // The Initialize call in AgentContextPropagationBehavior throws when the same
        // scoped context is re-init'd with a different agent. This is the scope-leak
        // detection that pre-dates PR-1; PR-1 must not weaken it.
        await using var provider = BuildPipeline(DevelopmentConfig());
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        var act = () => mediator.Send(new TestAgentScopedRequest("agent-2", "conv-1", 1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*scope conflict*");
    }

    [Fact]
    public async Task Pipeline_NoCredentialProvidersRegistered_ResolutionFailureThrows()
    {
        // Build pipeline without registering a credential provider — resolver returns
        // NoProvidersRegistered, behavior throws.
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var appConfig = new AppConfig { AI = new AIConfig { Identity = DevelopmentConfig() } };
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig));
        services.AddSingleton<IHostEnvironment>(
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development"));
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();

        // Resolver registered, but zero credential providers
        services.AddSingleton<IAgentIdentityResolver, EntraAgentIdResolver>();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblyContaining<AgentIdentityIntegrationTests>());
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentContextPropagationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AgentIdentityResolutionBehavior<,>));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var act = () => mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolution failed*")
            .WithMessage("*no_providers_registered*");
    }

    [Fact]
    public async Task Pipeline_TwoAmbients_HumanCallerScopeAndAgentIdentityCoexist()
    {
        // Task #6 established the human-caller scope (IKnowledgeScope, tenant/user); PR-1
        // adds the agent-identity scope (IAgentExecutionContext.AgentIdentity). After this
        // request the BOTH ambients are populated and INDEPENDENTLY readable.
        await using var provider = BuildPipeline(DevelopmentConfig(), registerKnowledgeScope: true);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        var knowledgeScope = scope.ServiceProvider.GetRequiredService<IKnowledgeScope>();

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        // Agent identity stamped by PR-1's behavior.
        execContext.AgentIdentity.Should().NotBeNull();
        execContext.AgentIdentity!.Id.Should().Be("dev-agent");

        // Human-caller scope untouched by PR-1's behavior; it stayed at the value
        // pre-set by the fake (mirrors how KnowledgeScopeMiddleware would populate it
        // from ClaimsPrincipal).
        knowledgeScope.UserId.Should().Be("alice");
        knowledgeScope.TenantId.Should().Be("tenant-contoso");
    }

    [Fact]
    public async Task Pipeline_AfterResolution_ValidatorDeniesUnauthorisedTool()
    {
        // End-to-end: resolve identity, then check the validator against that identity.
        // No allowlist for "dev-agent" → fail-closed deny.
        await using var provider = BuildPipeline(DevelopmentConfig());
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        var validator = scope.ServiceProvider.GetRequiredService<IAgentIdentityValidator>();

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        validator.CanInvoke(execContext.AgentIdentity!, "file_system").Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_AfterResolution_ValidatorAllowsAuthorisedTool()
    {
        var config = DevelopmentConfig();
        config.ToolAuthorization.AllowedToolsByAgentId["dev-agent"] = new[] { "file_system" };

        await using var provider = BuildPipeline(config);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var execContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        var validator = scope.ServiceProvider.GetRequiredService<IAgentIdentityValidator>();

        await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));

        validator.CanInvoke(execContext.AgentIdentity!, "file_system").Should().BeTrue();
        validator.CanInvoke(execContext.AgentIdentity!, "shell_exec").Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_DifferentScopesGetIndependentContexts()
    {
        // Each request scope must produce its own AgentExecutionContext (Scoped lifetime),
        // so identity from scope A does NOT leak into scope B even when they belong to the
        // same root provider.
        await using var provider = BuildPipeline(DevelopmentConfig());

        using (var scopeA = provider.CreateScope())
        {
            var mediator = scopeA.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new TestAgentScopedRequest("agent-1", "conv-1", 1));
            scopeA.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .AgentIdentity.Should().NotBeNull();
        }

        using (var scopeB = provider.CreateScope())
        {
            // Fresh scope → fresh context → no identity yet
            var freshContext = scopeB.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            freshContext.AgentIdentity.Should().BeNull();

            var mediator = scopeB.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new TestAgentScopedRequest("agent-2", "conv-2", 1));

            freshContext.AgentIdentity.Should().NotBeNull();
            freshContext.AgentIdentity!.Id.Should().Be("dev-agent");
        }
    }

    // --- Test handler + request types ---------------------------------------

    public sealed record TestAgentScopedRequest(
        string AgentId,
        string ConversationId,
        int TurnNumber) : IRequest<TestResponse>, IAgentScopedRequest;

    public sealed record NonAgentRequest : IRequest<TestResponse>;

    public sealed record TestResponse(bool Ok);

    public sealed class TestAgentRequestHandler
        : IRequestHandler<TestAgentScopedRequest, TestResponse>
    {
        public Task<TestResponse> Handle(TestAgentScopedRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new TestResponse(true));
    }

    public sealed class NonAgentRequestHandler
        : IRequestHandler<NonAgentRequest, TestResponse>
    {
        public Task<TestResponse> Handle(NonAgentRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new TestResponse(true));
    }

    private sealed class FakeKnowledgeScope : IKnowledgeScope
    {
        public string? UserId { get; init; }
        public string? TenantId { get; init; }
        public string? DatasetId { get; init; }
        public string? DatasetName { get; init; }
        public string? DatasetOwnerId { get; init; }
        public string? AgentId { get; init; }
        public string? ConversationId { get; init; }
    }
}
