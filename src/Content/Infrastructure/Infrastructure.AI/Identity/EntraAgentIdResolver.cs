using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Identity;

/// <summary>
/// Default <see cref="IAgentIdentityResolver"/> — orchestrates the registered
/// <see cref="IAgentCredentialProvider"/>s in a fixed credential-hierarchy order
/// (federated → managed identity → certificate → client secret → development) and
/// returns the first successfully-resolved <see cref="AgentIdentity"/>. The hierarchy
/// order is invariant; a consumer can register fewer providers but cannot reorder
/// the ones they register.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentIdentityKind.Development"/> is only honoured when the host
/// environment is Development. The check happens in this resolver (not just in
/// <see cref="DevelopmentAgentCredentialProvider"/>) so a misconfigured production
/// registration is short-circuited before the provider's <c>ResolveAsync</c> runs.
/// </para>
/// <para>
/// Providers with <see cref="AgentIdentityKind.Unspecified"/> are always skipped —
/// the sentinel kind indicates a misregistered provider.
/// </para>
/// <para>
/// Per-provider failure results are collected and concatenated into the final
/// <c>agent_identity.no_provider_succeeded</c> failure so a consumer can diagnose
/// which providers were tried and why each rejected.
/// </para>
/// </remarks>
public sealed class EntraAgentIdResolver : IAgentIdentityResolver
{
    /// <summary>Stable code returned when no registered provider succeeds.</summary>
    public const string NoProviderSucceededCode = "agent_identity.no_provider_succeeded";

    /// <summary>Stable code returned when no providers are registered at all.</summary>
    public const string NoProvidersRegisteredCode = "agent_identity.no_providers_registered";

    private static readonly AgentIdentityKind[] s_priorityOrder =
    [
        AgentIdentityKind.FederatedCredential,
        AgentIdentityKind.ManagedIdentity,
        AgentIdentityKind.Certificate,
        AgentIdentityKind.ClientSecret,
        AgentIdentityKind.Development
    ];

    private readonly IReadOnlyDictionary<AgentIdentityKind, IAgentCredentialProvider> _providersByKind;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<EntraAgentIdResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntraAgentIdResolver"/> class.
    /// </summary>
    /// <param name="providers">All registered credential providers.</param>
    /// <param name="hostEnvironment">Host environment used to gate the Development kind.</param>
    /// <param name="logger">Logger for resolution diagnostics.</param>
    /// <exception cref="InvalidOperationException">Two or more providers register the same
    /// <see cref="AgentIdentityKind"/>. The contract is one provider per kind; a duplicate
    /// would silently shadow the other in the priority walk.</exception>
    public EntraAgentIdResolver(
        IEnumerable<IAgentCredentialProvider> providers,
        IHostEnvironment hostEnvironment,
        ILogger<EntraAgentIdResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        var materialised = providers.ToArray();
        var byKind = new Dictionary<AgentIdentityKind, IAgentCredentialProvider>();
        foreach (var provider in materialised)
        {
            if (byKind.TryGetValue(provider.Kind, out var existing))
                throw new InvalidOperationException(
                    $"Multiple IAgentCredentialProvider instances registered for kind " +
                    $"{provider.Kind}: '{existing.GetType().FullName}' and " +
                    $"'{provider.GetType().FullName}'. The contract is one provider per kind " +
                    $"— the credential hierarchy walk would silently shadow the second one. " +
                    $"Remove the duplicate registration in DI.");

            byKind[provider.Kind] = provider;
        }

        _providersByKind = byKind;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<AgentIdentity>> ResolveAsync(
        CredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_providersByKind.Count == 0)
        {
            _logger.LogError("EntraAgentIdResolver has no registered IAgentCredentialProvider instances.");
            return Result<AgentIdentity>.Fail(NoProvidersRegisteredCode);
        }

        var attempts = new List<string>();

        foreach (var kind in s_priorityOrder)
        {
            if (kind == AgentIdentityKind.Development && !_hostEnvironment.IsDevelopment())
            {
                // Don't even ask the Development provider in non-Development environments.
                // The provider itself also refuses, but short-circuiting here keeps the
                // failure code stable and the audit trail simpler.
                continue;
            }

            if (!_providersByKind.TryGetValue(kind, out var provider))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var result = await provider.ResolveAsync(context, cancellationToken);
            if (result.IsSuccess && result.Value is not null)
            {
                _logger.LogInformation(
                    "Resolved agent identity {IdentityId} via {Kind} provider.",
                    result.Value.Id, kind);
                return result;
            }

            attempts.Add(result.Errors.Count == 0
                ? $"{kind}: <no error details>"
                : $"{kind}: {string.Join(", ", result.Errors)}");
        }

        var summary = attempts.Count == 0
            ? "no providers in the configured hierarchy were registered"
            : string.Join(" | ", attempts);

        _logger.LogWarning(
            "EntraAgentIdResolver exhausted the credential hierarchy: {Attempts}", summary);

        return Result<AgentIdentity>.Fail($"{NoProviderSucceededCode} ({summary})");
    }
}
