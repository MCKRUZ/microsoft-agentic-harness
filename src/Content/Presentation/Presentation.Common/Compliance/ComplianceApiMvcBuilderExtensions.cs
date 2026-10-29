using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.Compliance;

/// <summary>
/// Deliberate opt-in mount for the compliance API: <see cref="ComplianceController"/> and
/// <see cref="ComplianceExportController"/>. Every host that holds personal data chains <see cref="AddComplianceApi"/>
/// onto its <c>AddControllers()</c> call, so a person's data in that process can be erased and copied (#1003).
/// </summary>
public static class ComplianceApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts the compliance controllers: registers <see cref="ComplianceApiMarker"/>, which arms their routes, and
    /// <see cref="OperatorGate"/>, and adds this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <param name="sharedIdentitySchemes">
    /// The host's sign-in bypass schemes, under which every caller is the same synthetic person; self-service and operator
    /// requests are refused under them. Pass an empty list only for a host with no bypass at all.
    /// </param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// A request erases or copies only what this process holds; the receipt and the copy name the host. Mount it in every
    /// host whose stores hold personal data, and send each person's request to each of them.
    /// </para>
    /// <para>
    /// The controllers carry no rate limits of their own. Add <see cref="ComplianceRateLimits.Create"/> to the host's
    /// global limiter, or the endpoints run unthrottled.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The API was already mounted.</exception>
    public static IMvcBuilder AddComplianceApi(this IMvcBuilder builder, IEnumerable<string> sharedIdentitySchemes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sharedIdentitySchemes);

        // A second mount could name different schemes; the first would silently win. Refuse rather than guess.
        if (builder.Services.Any(d => d.ServiceType == typeof(ComplianceApiMarker)))
            throw new InvalidOperationException("The compliance API is already mounted in this host.");

        builder.Services.AddSingleton(new ComplianceApiMarker(sharedIdentitySchemes));
        // One gate for every operator action on someone else's data (erasure, document removal, export), so the role,
        // tenant and bypass rules cannot drift between them.
        builder.Services.TryAddSingleton<OperatorGate>();

        return builder.AddApplicationPart(typeof(ComplianceController).Assembly);
    }
}
