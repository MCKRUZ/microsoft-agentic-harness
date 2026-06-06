using System.Security.Cryptography.X509Certificates;
using Application.AI.Common.Interfaces.Identity;
using Azure.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Identity;

/// <summary>
/// X.509 client-certificate credential provider — uses
/// <see cref="ClientCertificateCredential"/> for hybrid scenarios where the agent
/// runs outside Azure but still authenticates to Entra ID. The certificate is
/// loaded either from the Windows certificate store (by thumbprint) or from a
/// filesystem path (PEM/PFX).
/// </summary>
/// <remarks>
/// Exactly one of <c>CertificateThumbprint</c> or <c>CertificatePath</c> must be
/// set. The provider reports itself unconfigured if both or neither are present so
/// the resolver moves to the next kind rather than silently picking one.
/// </remarks>
public sealed class CertificateCredentialProvider : IAgentCredentialProvider
{
    /// <summary>Stable code returned when config is missing required fields.</summary>
    public const string NotConfiguredCode = "agent_identity.certificate_not_configured";

    /// <summary>Stable code returned when the certificate cannot be loaded.</summary>
    public const string CertificateLoadFailedCode = "agent_identity.certificate_load_failed";

    /// <summary>Stable code returned when constructing the underlying credential fails.</summary>
    public const string CredentialConstructionFailedCode = "agent_identity.certificate_credential_failed";

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<CertificateCredentialProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateCredentialProvider"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration monitor.</param>
    /// <param name="logger">Logger for diagnostic events.</param>
    public CertificateCredentialProvider(
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<CertificateCredentialProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public AgentIdentityKind Kind => AgentIdentityKind.Certificate;

    /// <inheritdoc />
    public Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var certConfig = _appConfig.CurrentValue.AI?.Identity?.Certificate;
        if (certConfig is null
            || string.IsNullOrWhiteSpace(certConfig.AgentId)
            || string.IsNullOrWhiteSpace(certConfig.TenantId)
            || string.IsNullOrWhiteSpace(certConfig.ClientId))
        {
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));
        }

        var hasThumbprint = !string.IsNullOrWhiteSpace(certConfig.CertificateThumbprint);
        var hasPath = !string.IsNullOrWhiteSpace(certConfig.CertificatePath);

        // Exactly one must be set — both or neither is a misconfig the resolver should
        // treat as unconfigured so it can move on to the next kind in the hierarchy.
        if (hasThumbprint == hasPath)
            return Task.FromResult(Result<AgentIdentity>.Fail(NotConfiguredCode));

        X509Certificate2 cert;
        try
        {
            cert = hasThumbprint
                ? LoadFromStore(certConfig.CertificateThumbprint!)
                : LoadFromFile(certConfig.CertificatePath!, certConfig.CertificatePassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load X.509 certificate for agent {AgentId}.",
                certConfig.AgentId);
            return Task.FromResult(Result<AgentIdentity>.Fail(CertificateLoadFailedCode));
        }

        try
        {
            _ = new ClientCertificateCredential(certConfig.TenantId, certConfig.ClientId, cert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to construct ClientCertificateCredential for agent {AgentId}.",
                certConfig.AgentId);
            return Task.FromResult(Result<AgentIdentity>.Fail(CredentialConstructionFailedCode));
        }

        var identity = new AgentIdentity
        {
            Id = certConfig.AgentId,
            Kind = AgentIdentityKind.Certificate,
            TenantId = certConfig.TenantId,
            ObjectId = certConfig.ObjectId,
            Audience = string.IsNullOrEmpty(context.Audience) ? null : context.Audience
        };

        return Task.FromResult(Result<AgentIdentity>.Success(identity));
    }

    private static X509Certificate2 LoadFromStore(string thumbprint)
    {
        // Search CurrentUser first, then LocalMachine. Both stores searched with
        // validation off — operators must trust the certs they install in their stores.
        var normalised = thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalised, validOnly: false);
            if (matches.Count > 0)
                return matches[0];
        }

        throw new InvalidOperationException(
            $"No certificate with thumbprint '{normalised}' found in CurrentUser or LocalMachine 'My' stores.");
    }

    private static X509Certificate2 LoadFromFile(string path, string? password)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Certificate file '{path}' does not exist.", path);

        return string.IsNullOrEmpty(password)
            ? X509CertificateLoader.LoadCertificateFromFile(path)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}
