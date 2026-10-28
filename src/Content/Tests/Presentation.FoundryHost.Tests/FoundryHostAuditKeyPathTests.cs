using Application.Core.Validation;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Presentation.FoundryHost;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// The audit pseudonym key under FoundryHost (#996): it must outlive a container restart, as the audit trails do, and the
/// path it is given must still pass the rule that keeps the key out of every audit folder.
/// </summary>
public sealed class FoundryHostAuditKeyPathTests
{
    private const string Home = "/home/agent";

    private static AppConfig BoundFromOverrides()
    {
        var overrides = FoundryHostBootstrap.BuildConfigOverrides(name => name == "HOME" ? Home : null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides.Select(o =>
                new KeyValuePair<string, string?>(o.Key.Replace("__", ":", StringComparison.Ordinal), o.Value)))
            .Build();
        return configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_RootsThePseudonymKeyUnderTheStateRoot()
    {
        var config = BoundFromOverrides();

        config.AI.Audit.PseudonymKeyPath.Should().StartWith($"{Home}/agent-state/",
            "only $HOME survives Foundry's idle/resume, and a key made anew at every start breaks every reference made before it");
    }

    [Fact]
    public void BuildConfigOverrides_WithHome_ThePseudonymKeyPathPassesTheKeyValidator()
    {
        var config = BoundFromOverrides();

        var result = new AuditPseudonymKeyValidator().Validate(config.AI);

        result.IsValid.Should().BeTrue(
            "the rooted key must sit outside every rooted audit folder: " + string.Join("; ", result.Errors));
    }
}
