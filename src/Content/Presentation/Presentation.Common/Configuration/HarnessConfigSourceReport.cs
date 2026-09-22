using Microsoft.Extensions.Configuration;

namespace Presentation.Common.Configuration;

/// <summary>
/// Records which <see cref="IConfiguration"/> providers actually loaded for this host, captured at
/// the moment <see cref="Extensions.IServiceCollectionExtensions.GetServices"/> builds the real
/// configuration root. Lets a running deployment verify — not merely assert — that no Azure
/// configuration source is active, via a health endpoint (issue #591).
/// </summary>
/// <remarks>
/// Reads <c>((IConfigurationRoot)config).Providers</c> from the configuration
/// <see cref="Helpers.AppConfigHelper.LoadAppConfig"/> actually built. A host's
/// <c>WebApplicationBuilder</c> has its own, separate <see cref="IConfigurationRoot"/> that never
/// sees the Azure sources <c>LoadAppConfig</c> conditionally adds — inspecting that one instead
/// would always report "no Azure providers loaded", true or not.
/// </remarks>
/// <param name="ProviderTypeNames">
/// The type name of every configuration provider that loaded, in provider order — diagnostic
/// detail for the health endpoint. Type names only — never a value a provider carries (a
/// connection string, a Key Vault URI).
/// </param>
/// <param name="AzureKeyVaultLoaded">
/// Whether the Azure Key Vault configuration provider loaded, checked against the actual provider
/// type (see <see cref="FromConfigurationRoot"/>) rather than a string comparison — a future rename
/// of that type fails the build instead of this silently always reporting <c>false</c>.
/// </param>
/// <param name="AzureAppConfigurationLoaded">Whether the Azure App Configuration provider loaded — see <see cref="AzureKeyVaultLoaded"/>.</param>
public sealed record HarnessConfigSourceReport(
    IReadOnlyList<string> ProviderTypeNames,
    bool AzureKeyVaultLoaded,
    bool AzureAppConfigurationLoaded)
{
    /// <summary>Builds a report from a built <see cref="IConfigurationRoot"/>.</summary>
    public static HarnessConfigSourceReport FromConfigurationRoot(IConfigurationRoot configurationRoot)
    {
        var providers = configurationRoot.Providers.ToArray();

        return new HarnessConfigSourceReport(
            ProviderTypeNames: providers.Select(p => p.GetType().Name).ToArray(),
            AzureKeyVaultLoaded: providers.Any(p =>
                p is Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationProvider),
            // AzureAppConfigurationProvider itself is internal to its package; its public
            // extensibility marker interface is the compiler-checked substitute.
            AzureAppConfigurationLoaded: providers.Any(p =>
                p is Microsoft.Extensions.Configuration.AzureAppConfiguration.IConfigurationRefresherProvider));
    }
}
