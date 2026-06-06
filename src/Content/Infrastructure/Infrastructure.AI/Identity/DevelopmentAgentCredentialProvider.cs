using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Fixture-identity credential provider — returns a static <see cref="AgentIdentity"/>
/// from config without contacting Entra. Honoured only when the host environment is
/// Development; refuses to produce an identity in any other environment so a misconfigured
/// production deployment cannot silently fall back to a dev fixture.
/// </summary>
/// <remarks>
/// Useful locally so the rest of the harness behaves identically with the identity
/// subsystem enabled, even before real Entra Agent ID credentials are wired up.
/// Returns failure results with stable <c>agent_identity.*</c> error codes — full
/// exception details (none expected here) would be logged via structured logging
/// if any token-acquisition path were added later.
/// </remarks>
public sealed class DevelopmentAgentCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Stable code returned when the host environment is not Development.</summary>
    public const string EnvNotDevelopmentCode = "agent_identity.development_provider_unavailable_in_production";

    /// <summary>Stable code returned when the agent id is missing or whitespace.</summary>
    public const string NotConfiguredCode = "agent_identity.development_provider_not_configured";

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<DevelopmentAgentCredentialProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevelopmentAgentCredentialProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="hostEnvironment">Host environment used for the Development check.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    public DevelopmentAgentCredentialProvider(
        IOptionsMonitor<AppConfig> appConfig,
        IHostEnvironment hostEnvironment,
        ILogger<DevelopmentAgentCredentialProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentIdentityKind Kind => AgentIdentityKind.Development;

    /// <inheritdoc />
    public Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_hostEnvironment.IsDevelopment())
        {
            _logger.LogWarning(
                "DevelopmentAgentCredentialProvider invoked in non-Development environment '{Env}'; refusing.",
                _hostEnvironment.EnvironmentName);
            return Task.FromResult(Result<AgentIdentity>.Fail(EnvNotDevelopmentCode));
        }

        var devConfig = _appConfig.CurrentValue.AI?.Identity?.Development;
        if (devConfig is null || string.IsNullOrWhiteSpace(devConfig.AgentId))
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));

        var identity = new AgentIdentity
        {
            Id = devConfig.AgentId,
            Kind = AgentIdentityKind.Development,
            TenantId = devConfig.TenantId,
            ObjectId = devConfig.ObjectId,
            Audience = string.IsNullOrEmpty(context.Audience) ? null : context.Audience
        };

        return Task.FromResult(Result<AgentIdentity>.Success(identity));
    }
}
