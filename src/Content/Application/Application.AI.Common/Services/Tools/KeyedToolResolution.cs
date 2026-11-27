using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The one way to build a first-party tool from its keyed registration (#888): refuses a tool whose reported name
/// differs from its key.
/// </summary>
/// <remarks>
/// <para>
/// Grants, deny rules and manifests name a tool by a single string. A string that was a tool's key and another
/// tool's name could not be read one way, so a grant meant for one tool could cover another. Requiring key and
/// name to agree (case aside) makes the two readings the same tool by construction, which is also why nothing in
/// the permission code needs to translate one into the other.
/// </para>
/// <para>
/// Checked here, when a tool is built, rather than by building every tool at startup: an earlier attempt that built
/// them all (#524) stopped hosts from booting when one tool's dependencies were not wired in that host. A refused
/// tool is reported by whichever caller asked for it and is unavailable everywhere. <c>KeyedToolResolutionIsTheOnlyWayInTests</c>
/// keeps every other way of building a keyed tool out of production code.
/// </para>
/// <para>
/// A tool's factory can fail in any way its dependencies can: a tool registered in every host may need a service only
/// one host provides (the render tools need <c>IClientToolBridge</c>, which only AgentHub registers). That failure is
/// handed back, not thrown, for the same reason #627 made <c>FirstPartyToolLookup.TryResolve</c> catch it: a caller
/// that only expected "no such tool" let it escape and fail a whole agent turn over one tool the agent never called
/// (#1072). The out parameter makes every caller say what it does with it.
/// </para>
/// </remarks>
public static class KeyedToolResolution
{
    /// <summary>
    /// Builds the tool registered under <paramref name="key"/>, or returns <c>null</c> when there is none or it cannot
    /// be built in this host.
    /// </summary>
    /// <param name="services">The provider to resolve from.</param>
    /// <param name="key">The tool's registration key.</param>
    /// <param name="constructionError">
    /// The exception the tool's factory threw, or <c>null</c> when the tool was built or nothing is registered under
    /// <paramref name="key"/>. Telling the two <c>null</c> results apart is what lets a caller report a tool this host
    /// cannot build differently from a name nobody registered.
    /// </param>
    /// <returns>The tool, or <c>null</c>.</returns>
    /// <exception cref="ToolNameMismatchException">The tool reports a name other than <paramref name="key"/>.</exception>
    public static ITool? Resolve(IServiceProvider services, string key, out Exception? constructionError)
    {
        ArgumentNullException.ThrowIfNull(services);

        constructionError = null;
        ITool? tool;
        try
        {
            tool = services.GetKeyedService<ITool>(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            constructionError = ex;
            return null;
        }

        if (tool is not null && !string.Equals(tool.Name, key, StringComparison.OrdinalIgnoreCase))
            throw new ToolNameMismatchException(key, tool.Name);

        return tool;
    }
}
