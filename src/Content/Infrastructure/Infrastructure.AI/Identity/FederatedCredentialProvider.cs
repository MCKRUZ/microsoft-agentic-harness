using Application.AI.Common.Interfaces.Identity;
using Azure.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Federated workload-identity credential provider — uses
/// <see cref="WorkloadIdentityCredential"/> for OIDC token-exchange flows
/// (AKS workload identity, GitHub Actions OIDC, Azure DevOps OIDC). The most
/// preferred credential kind because no secret is stored anywhere.
/// </summary>
/// <remarks>
/// The federated token file is supplied either via <c>FederatedProviderConfig.TokenFilePath</c>
/// (explicit) or via the <c>AZURE_FEDERATED_TOKEN_FILE</c> environment variable that the
/// runtime sets (AKS default). The provider does not validate the file's contents —
/// the Azure SDK reads it lazily at first token acquisition.
/// </remarks>
public sealed class FederatedCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Stable code returned when config is missing required fields.</summary>
    public const string NotConfiguredCode = "agent_identity.federated_credential_not_configured";

    /// <summary>Stable code returned when constructing the underlying credential fails.</summary>
    public const string CredentialConstructionFailedCode = "agent_identity.federated_credential_credential_failed";

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<FederatedCredentialProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedCredentialProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    public FederatedCredentialProvider(
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<FederatedCredentialProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentIdentityKind Kind => AgentIdentityKind.FederatedCredential;

    /// <inheritdoc />
    public Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var fedConfig = _appConfig.CurrentValue.AI?.Identity?.FederatedCredential;
        if (fedConfig is null
            || string.IsNullOrWhiteSpace(fedConfig.AgentId)
            || string.IsNullOrWhiteSpace(fedConfig.TenantId)
            || string.IsNullOrWhiteSpace(fedConfig.ClientId))
        {
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));
        }

        try
        {
            var options = new WorkloadIdentityCredentialOptions
            {
                TenantId = fedConfig.TenantId,
                ClientId = fedConfig.ClientId,
                TokenFilePath = fedConfig.TokenFilePath
            };
            _ = new WorkloadIdentityCredential(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to construct WorkloadIdentityCredential for agent {AgentId}.",
                fedConfig.AgentId);
            return Task.FromResult(Result<AgentIdentity>.Fail(CredentialConstructionFailedCode));
        }

        var identity = new AgentIdentity
        {
            Id = fedConfig.AgentId,
            Kind = AgentIdentityKind.FederatedCredential,
            TenantId = fedConfig.TenantId,
            ObjectId = fedConfig.ObjectId,
            Audience = string.IsNullOrEmpty(context.Audience) ? null : context.Audience
        };

        return Task.FromResult(Result<AgentIdentity>.Success(identity));
    }
}
