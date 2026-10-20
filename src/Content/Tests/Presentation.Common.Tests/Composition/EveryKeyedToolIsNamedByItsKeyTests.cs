using Application.AI.Common.Exceptions;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Every first-party tool the real composition root registers reports its registration key as its name (#888).
/// </summary>
/// <remarks>
/// A tool that did not would be refused wherever it is built (<c>KeyedToolResolution</c>), so an agent declaring it
/// would fail to build. This catches that in CI, for the tools this repository ships, instead of at a host's first
/// use. A tool that cannot be constructed in this test host (a dependency only another host wires) goes unchecked
/// here, so each one is named in <see cref="NotConstructibleHere"/> with its reason rather than skipped silently.
/// </remarks>
public sealed class EveryKeyedToolIsNamedByItsKeyTests
{
    /// <summary>Tools this composition cannot build, and why. Exact: an entry that starts building must be removed.</summary>
    private const string NeedsTheBrowserBridge =
        "needs IClientToolBridge, which only AgentHub registers (it relays the call to the browser)";

    private static readonly IReadOnlyDictionary<string, string> NotConstructibleHere = new Dictionary<string, string>
    {
        ["dashboard_control"] = NeedsTheBrowserBridge,
        ["render_chart"] = NeedsTheBrowserBridge,
        ["render_image"] = NeedsTheBrowserBridge,
        ["render_form"] = NeedsTheBrowserBridge,
        ["render_table"] = NeedsTheBrowserBridge,
        ["list_metrics"] = "needs IMetricCatalog, which only AgentHub registers (the dashboard's metric list)",
    };

    [Fact]
    public async Task EveryRegisteredFirstPartyTool_ReportsItsKeyAsItsName()
    {
        // Async disposal: the composition holds services that only implement IAsyncDisposable.
        // A GitOps controller is named so the GitOps tools can be built and checked rather than excused.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:GitOps:ActiveController"] = "flux",
        });
        var keys = provider.GetRequiredService<FirstPartyToolLookup>().RegisteredFirstPartyToolKeys;

        var mismatched = new List<string>();
        var unbuilt = new List<string>();
        var built = 0;
        foreach (var key in keys)
        {
            try
            {
                if (KeyedToolResolution.Resolve(provider, key) is not null)
                    built++;
            }
            catch (ToolNameMismatchException ex)
            {
                mismatched.Add($"{ex.Key} reports '{ex.Name}'");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                unbuilt.Add($"{key}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        mismatched.Should().BeEmpty("a tool whose name differs from its key is refused wherever it is built");
        unbuilt.Select(u => u[..u.IndexOf(':')]).Should().BeEquivalentTo(
            NotConstructibleHere.Keys,
            "a tool this host cannot build goes unchecked, so each one must be named with its reason; " +
            "a listed tool that now builds must leave the list. Unbuilt: " + string.Join("; ", unbuilt));
        built.Should().BeGreaterThan(20, "the check must have built the shipped tools, not skipped them all");
    }
}
