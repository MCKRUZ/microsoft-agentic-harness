using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Common.Tests;

/// <summary>
/// Identifies a DI service descriptor as one the Agent 365 vendor distro itself registered — shared
/// between <c>Agent365ExporterWiringTests</c> and <c>ValidateOnBuildSweepTests</c>, which previously
/// each carried their own copy of this exact check.
/// </summary>
/// <remarks>
/// Matches ONLY the vendor's own namespace prefix, not a bare "Agent365" substring. Empirically
/// confirmed against the pinned <c>Microsoft.OpenTelemetry</c> 1.1.0 package (reflected its exported
/// types directly): every Agent 365 type — <c>Agent365Exporter</c>, <c>Agent365ExporterCore</c>,
/// <c>Agent365DurableDelivery</c>, and so on — lives under
/// <c>Microsoft.Agents.A365.Observability.Runtime.*</c>. A bare substring match also matches this
/// repo's OWN <c>Infrastructure.Observability.Agent365.Agent365TelemetryAttribution</c>, which
/// <c>Infrastructure.Observability/DependencyInjection.cs</c> registers UNCONDITIONALLY — so a
/// substring-based non-vacuity proof run against the full composition root (as
/// <c>ValidateOnBuildSweepTests</c> does) would pass identically whether or not Agent 365 export is
/// actually enabled, precisely the vacuity failure it exists to rule out. Found by a THIRD altitude
/// pass on #738's own review: the original duplicated helper happened to be safe in
/// <c>Agent365ExporterWiringTests</c> only by coincidence, because that class builds services via
/// <c>AddWebTelemetry</c> directly on a bare collection and never invokes the DI module that registers
/// the harness's own always-on attribution type.
/// </remarks>
internal static class Agent365ServiceMatcher
{
    private const string VendorNamespacePrefix = "Microsoft.Agents.A365.";

    public static bool IsAgent365Service(ServiceDescriptor descriptor)
        => IsVendorType(descriptor.ServiceType) || IsVendorType(descriptor.ImplementationType);

    private static bool IsVendorType(Type? type)
        => type?.FullName?.StartsWith(VendorNamespacePrefix, StringComparison.Ordinal) == true;
}
