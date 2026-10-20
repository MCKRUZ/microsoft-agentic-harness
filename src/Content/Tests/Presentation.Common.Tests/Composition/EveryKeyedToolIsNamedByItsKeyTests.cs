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
/// use. A tool that cannot be constructed in this test host (a dependency it does not wire) is not a mismatch and
/// is left to the hosts that wire it; the floor below keeps the check from passing on a host that built nothing.
/// </remarks>
public sealed class EveryKeyedToolIsNamedByItsKeyTests
{
    [Fact]
    public async Task EveryRegisteredFirstPartyTool_ReportsItsKeyAsItsName()
    {
        // Async disposal: the composition holds services that only implement IAsyncDisposable.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());
        var keys = provider.GetRequiredService<FirstPartyToolLookup>().RegisteredFirstPartyToolKeys;

        var mismatched = new List<string>();
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
                // Not constructible in this host; not what this test is about.
            }
        }

        mismatched.Should().BeEmpty("a tool whose name differs from its key is refused wherever it is built");
        built.Should().BeGreaterThan(20, "the check must have built the shipped tools, not skipped them all");
    }
}
