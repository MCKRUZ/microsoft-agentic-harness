using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using FluentAssertions;
using Presentation.Common.Configuration;
using Xunit;

namespace Presentation.Common.Tests.Configuration;

/// <summary>
/// Tests for <see cref="HarnessConfigSourceReport.FromConfigurationRoot"/>, in particular the
/// Azure App Configuration detection a correctness review found always reported <c>false</c>
/// (issue #591 follow-up): it checked configuration providers against
/// <see cref="IConfigurationRefresherProvider"/>, an interface the real provider
/// (<c>AzureAppConfigurationProvider</c>, internal to its package) never implements — only a
/// separate helper object does. The real provider implements <see cref="IConfigurationRefresher"/>
/// directly, which is what detection must check instead.
/// </summary>
public sealed class HarnessConfigSourceReportTests
{
    [Fact]
    public void FromConfigurationRoot_NoProvidersLoaded_ReportsNothingLoaded()
    {
        var configuration = (IConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var report = HarnessConfigSourceReport.FromConfigurationRoot(configuration);

        report.AzureKeyVaultLoaded.Should().BeFalse();
        report.AzureAppConfigurationLoaded.Should().BeFalse();
    }

    [Fact]
    public void FromConfigurationRoot_AzureAppConfigurationProviderPresent_ReportsLoaded()
    {
        // A hand-built stand-in for the real (internal) AzureAppConfigurationProvider, matching the
        // one public contract that provider actually implements. Exercising the real SDK provider
        // would require a live Azure App Configuration connection, which a unit test must not
        // depend on; this proves the detection logic itself against the exact interface shape the
        // shipped package exposes.
        var providers = new IConfigurationProvider[] { new FakeAzureAppConfigurationProvider() };
        var configuration = new ConfigurationRoot(providers);

        var report = HarnessConfigSourceReport.FromConfigurationRoot(configuration);

        report.AzureAppConfigurationLoaded.Should().BeTrue();
    }

    [Fact]
    public void FromConfigurationRoot_OnlyRefresherProviderHelperPresent_DoesNotReportLoaded()
    {
        // The bug this guards against: IConfigurationRefresherProvider is a different object
        // (used to look up refresh handles) that never appears among the providers a real Azure App
        // Configuration source registers into IConfigurationRoot.Providers. Detecting it would be
        // detecting the wrong thing, even though it happens to always evaluate to false today.
        var providers = new IConfigurationProvider[] { new FakeRefresherProviderOnly() };
        var configuration = new ConfigurationRoot(providers);

        var report = HarnessConfigSourceReport.FromConfigurationRoot(configuration);

        report.AzureAppConfigurationLoaded.Should().BeFalse();
    }

    /// <summary>Implements only what <see cref="IConfigurationProvider"/> and <see cref="IConfigurationRefresher"/> require.</summary>
    private sealed class FakeAzureAppConfigurationProvider : ConfigurationProvider, IConfigurationRefresher
    {
        public Uri AppConfigurationEndpoint => new("https://example.azconfig.io");

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void ProcessPushNotification(PushNotification pushNotification, TimeSpan? maxDelay = null)
        {
        }
    }

    /// <summary>Implements only <see cref="IConfigurationRefresherProvider"/> — the wrong marker.</summary>
    private sealed class FakeRefresherProviderOnly : ConfigurationProvider, IConfigurationRefresherProvider
    {
        public IEnumerable<IConfigurationRefresher> Refreshers => [];
    }
}
