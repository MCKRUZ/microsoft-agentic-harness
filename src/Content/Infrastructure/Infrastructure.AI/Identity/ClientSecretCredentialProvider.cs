using Application.AI.Common.Interfaces.Identity;
using Azure.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Client-secret credential provider — uses <see cref="ClientSecretCredential"/>.
/// Explicit last resort in the credential hierarchy: long-lived secret with no
/// automatic rotation. Emits a startup warning when configured in any environment
/// other than Development, so operators see the smell in their logs.
/// </summary>
/// <remarks>
/// <para>
/// The provider never logs the <c>ClientSecret</c> value. Diagnostic log lines
/// include the agent id, tenant id, and client id only.
/// </para>
/// <para>
/// Consumers should persist <c>ClientSecret</c> via user-secrets (development) or
/// Azure Key Vault (production), never <c>appsettings.json</c>.
/// </para>
/// </remarks>
public sealed class ClientSecretCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Stable code returned when config is missing required fields.</summary>
    public const string NotConfiguredCode = "agent_identity.client_secret_not_configured";

    /// <summary>Stable code returned when constructing the underlying credential fails.</summary>
    public const string CredentialConstructionFailedCode = "agent_identity.client_secret_credential_failed";

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ClientSecretCredentialProvider> _logger;
    private int _startupWarningEmitted;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientSecretCredentialProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="hostEnvironment">Host environment — drives the startup-warning gate.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    public ClientSecretCredentialProvider(
        IOptionsMonitor<AppConfig> appConfig,
        IHostEnvironment hostEnvironment,
        ILogger<ClientSecretCredentialProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentIdentityKind Kind => AgentIdentityKind.ClientSecret;

    /// <inheritdoc />
    public Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var secretConfig = _appConfig.CurrentValue.AI?.Identity?.ClientSecret;
        if (secretConfig is null
            || string.IsNullOrWhiteSpace(secretConfig.AgentId)
            || string.IsNullOrWhiteSpace(secretConfig.TenantId)
            || string.IsNullOrWhiteSpace(secretConfig.ClientId)
            || string.IsNullOrWhiteSpace(secretConfig.ClientSecret))
        {
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));
        }

        EmitStartupWarningIfNeeded(secretConfig.AgentId);

        try
        {
            _ = new ClientSecretCredential(
                secretConfig.TenantId,
                secretConfig.ClientId,
                secretConfig.ClientSecret);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to construct ClientSecretCredential for agent {AgentId}.",
                secretConfig.AgentId);
            return Task.FromResult(Result<AgentIdentity>.Fail(CredentialConstructionFailedCode));
        }

        var identity = new AgentIdentity
        {
            Id = secretConfig.AgentId,
            Kind = AgentIdentityKind.ClientSecret,
            TenantId = secretConfig.TenantId,
            ObjectId = secretConfig.ObjectId,
            Audience = string.IsNullOrEmpty(context.Audience) ? null : context.Audience
        };

        return Task.FromResult(Result<AgentIdentity>.Success(identity));
    }

    private void EmitStartupWarningIfNeeded(string agentId)
    {
        if (_hostEnvironment.IsDevelopment())
            return;

        // Interlocked guard so the warning fires once per process even when ResolveAsync
        // is called concurrently. The warning is a security smell signal, not a hot-path
        // log line — once is enough.
        if (Interlocked.Exchange(ref _startupWarningEmitted, 1) == 0)
        {
            _logger.LogWarning(
                "ClientSecretCredentialProvider is configured for agent {AgentId} in environment '{Env}'. " +
                "Client-secret credentials are an explicit last-resort in the credential hierarchy — " +
                "prefer federated workload identity, managed identity, or certificate. Rotate the " +
                "secret on a tight schedule and persist via Key Vault (never appsettings.json).",
                agentId, _hostEnvironment.EnvironmentName);
        }
    }
}
