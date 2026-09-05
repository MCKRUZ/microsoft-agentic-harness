using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The one place that scans a service collection for keyed <see cref="ITool"/> registration keys —
/// the bounded "what counts as a first-party tool name" set every consumer of that question needs.
/// </summary>
/// <remarks>
/// Consolidates a scan that had drifted into three independent copies: <see cref="FirstPartyToolLookup"/>'s
/// own registration (#387, which already consolidated three OTHER independent copies of the same
/// bounded-lookup pattern), <c>ToolCatalog</c>'s registration, and — until a code-review finding on
/// #524 — <c>PluginToolBoundaryStartupValidator</c>'s DI wiring. A fourth copy would be exactly the
/// drift #387 exists to prevent; every future caller should reach for this instead of re-deriving the
/// same <c>IsKeyedService &amp;&amp; ServiceType == typeof(ITool)</c> filter.
/// </remarks>
public static class KeyedToolRegistrationScan
{
    /// <summary>
    /// Every keyed-DI registration key registered as an <see cref="ITool"/> in <paramref name="services"/>.
    /// </summary>
    public static IEnumerable<string> Names(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ITool))
            .Select(descriptor => descriptor.ServiceKey as string)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!);
}
