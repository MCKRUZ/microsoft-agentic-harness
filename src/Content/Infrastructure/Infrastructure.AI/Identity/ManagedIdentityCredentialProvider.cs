using Application.AI.Common.Interfaces.Identity;
using Azure.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Azure managed-identity credential provider — uses
/// <see cref="ManagedIdentityCredential"/> for token acquisition. Honour-preferred
/// for Azure-hosted runtimes (App Service, Container Apps, AKS, VM) because Azure
/// rotates the credential automatically and the agent never sees a secret.
/// </summary>
/// <remarks>
/// The provider does NOT acquire a token during <see cref="ResolveAsync"/> — the
/// Azure SDK's <c>ManagedIdentityCredential</c> defers token requests until first
/// use. This keeps agent construction fast and avoids unnecessary IMDS calls.
/// Token acquisition happens when downstream consumers (HTTP outbound, A2A,
/// external APIs) explicitly request a token.
/// </remarks>
public sealed class ManagedIdentityCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Stable code returned when config is missing required fields.</summary>
    public const string NotConfiguredCode = "agent_identity.managed_identity_not_configured";

    /// <summary>Stable code returned when constructing the underlying credential fails.</summary>
    public const string CredentialConstructionFailedCode = "agent_identity.managed_identity_credential_failed";

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<ManagedIdentityCredentialProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedIdentityCredentialProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    public ManagedIdentityCredentialProvider(
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<ManagedIdentityCredentialProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentIdentityKind Kind => AgentIdentityKind.ManagedIdentity;

    /// <inheritdoc />
    public Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var miConfig = _appConfig.CurrentValue.AI?.Identity?.ManagedIdentity;
        if (miConfig is null || string.IsNullOrWhiteSpace(miConfig.AgentId))
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));

        try
        {
            // Constructing the credential is cheap and synchronous — no IMDS call here.
            // Token acquisition happens at first .GetTokenAsync() call by downstream code.
            _ = string.IsNullOrWhiteSpace(miConfig.ClientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(miConfig.ClientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to construct ManagedIdentityCredential for agent {AgentId}.",
                miConfig.AgentId);
            return Task.FromResult(Result<AgentIdentity>.Fail(CredentialConstructionFailedCode));
        }

        var identity = new AgentIdentity
        {
            Id = miConfig.AgentId,
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = miConfig.TenantId,
            ObjectId = miConfig.ObjectId,
            Audience = string.IsNullOrEmpty(context.Audience) ? null : context.Audience
        };

        return Task.FromResult(Result<AgentIdentity>.Success(identity));
    }
}
