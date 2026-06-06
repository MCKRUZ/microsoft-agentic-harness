using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Identity;
using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Identity;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Resolves the agent's workload <see cref="AgentIdentity"/> via the registered
/// <see cref="IAgentIdentityResolver"/> and stamps it onto the scoped
/// <see cref="IAgentExecutionContext"/> for the current request. Runs immediately
/// after <see cref="AgentContextPropagationBehavior{TRequest, TResponse}"/> so the
/// agent id is set before identity resolution begins; downstream behaviors
/// (audit, content safety, tool permission, governance) see a fully-stamped context.
/// </summary>
/// <remarks>
/// <para>
/// This is a pipeline behavior, not a factory hook, by design: the scoped
/// <see cref="IAgentExecutionContext"/> can only be safely injected into a service
/// that lives inside the request scope. <c>AgentFactory</c> is registered as
/// Singleton, so resolving the scoped context from its root <c>IServiceProvider</c>
/// returns a phantom instance — not the one the request actually consumes. The
/// pipeline behavior runs inside the request scope and gets the correct instance
/// via constructor injection.
/// </para>
/// <para>
/// Behaviour, gated by <c>AppConfig.AI.Identity.Enabled</c>:
/// </para>
/// <list type="bullet">
///   <item>Request is not <see cref="IAgentScopedRequest"/> → pass-through.</item>
///   <item><c>Identity.Enabled = false</c> → pass-through (existing behaviour).</item>
///   <item>Flag is on but resolver not registered → <see cref="InvalidOperationException"/>.</item>
///   <item>Identity already set on context (re-entrant request, e.g. nested
///   sub-agent) → pass-through; <see cref="IAgentExecutionContext.SetIdentity"/>
///   would early-return on value equality anyway, but skipping the resolver call
///   avoids the round-trip.</item>
///   <item>Resolver returns a failure result → <see cref="InvalidOperationException"/>
///   with the failure's stable error codes prepended.</item>
///   <item>Resolver succeeds → identity stamped on context, pipeline continues.</item>
/// </list>
/// <para>
/// Failure modes throw rather than returning a failure <c>Result</c> because the
/// caller chose to enable the subsystem — opting into identity is a binary security
/// guarantee, not best-effort. Silent fallback to "no identity" would defeat the
/// purpose. Implementations of <see cref="IAgentCredentialProvider"/> are
/// responsible for logging full exception details via structured logging; this
/// behavior only surfaces scrubbed codes in the exception message.
/// </para>
/// </remarks>
public sealed class AgentIdentityResolutionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IAgentIdentityResolver? _resolver;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<AgentIdentityResolutionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentIdentityResolutionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="executionContext">Scoped agent-execution context for the current request.</param>
    /// <param name="appConfig">Application configuration monitor for the identity flag.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    /// <param name="resolver">
    /// Optional credential-hierarchy resolver. Null when the consumer has not registered
    /// one — pass-through when the identity flag is off; fail-loud when the flag is on.
    /// </param>
    public AgentIdentityResolutionBehavior(
        IAgentExecutionContext executionContext,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<AgentIdentityResolutionBehavior<TRequest, TResponse>> logger,
        IAgentIdentityResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _executionContext = executionContext;
        _appConfig = appConfig;
        _logger = logger;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IAgentScopedRequest)
            return await next();

        var identityConfig = _appConfig.CurrentValue.AI?.Identity;
        if (identityConfig is null || !identityConfig.Enabled)
            return await next();

        if (_executionContext.AgentIdentity is not null)
            return await next();

        if (_resolver is null)
            throw new InvalidOperationException(
                "AppConfig.AI.Identity.Enabled is true but no IAgentIdentityResolver " +
                "is registered. Register a resolver in Infrastructure DI before enabling " +
                "the identity subsystem.");

        var credentialContext = new CredentialContext
        {
            Audience = identityConfig.DefaultAudience,
            Scopes = [.. identityConfig.DefaultScopes]
        };

        var resolution = await _resolver.ResolveAsync(credentialContext, cancellationToken);
        if (!resolution.IsSuccess || resolution.Value is null)
        {
            var errors = resolution.Errors.Count == 0
                ? "no error details"
                : string.Join("; ", resolution.Errors);
            throw new InvalidOperationException(
                $"Agent identity resolution failed: {errors}. " +
                "AppConfig.AI.Identity.Enabled is true so this is fatal — check " +
                "credential provider configuration.");
        }

        _executionContext.SetIdentity(resolution.Value);

        _logger.LogInformation(
            "Stamped agent identity {IdentityId} ({Kind}) onto execution context",
            resolution.Value.Id, resolution.Value.Kind);

        return await next();
    }
}
