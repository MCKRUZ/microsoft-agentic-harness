using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Guards the one line that separates a working host from a host that hangs on its first log call:
/// nothing in <c>ILocalLogRedactor</c>'s dependency graph may take an <c>ILogger&lt;T&gt;</c> (or any
/// other service that resolves <see cref="ILoggerFactory"/>) as a constructor parameter.
/// </summary>
/// <remarks>
/// <para>
/// <c>IServiceCollectionExtensions.WrapWithLocalLogRedaction</c> replaces the <see cref="ILoggerFactory"/>
/// registration with a factory delegate that resolves <c>ILocalLogRedactor</c> eagerly, and that
/// redactor depends on <c>ICompositeResponseSanitizer</c> and <c>IContentRedactionFilter</c>. Any
/// <c>ILogger&lt;T&gt;</c> constructor parameter anywhere under that graph re-enters the delegate that
/// is still constructing the factory. .NET's container does not detect a cycle hidden behind a factory
/// delegate — it re-enters forever — so the failure is a silent hang at first resolution, not an
/// exception.
/// </para>
/// <para>
/// <strong><see cref="ValidateOnBuildSweepTests"/> cannot catch this and was measured passing while it
/// was live</strong> — <c>ValidateOnBuild</c> builds call sites, and a factory descriptor terminates
/// call-site construction rather than being walked into or executed. This test resolves the factory for
/// real, which is the only instrument that sees it. It runs the resolution on its own thread so a
/// regression fails with this assertion instead of hanging the test host indefinitely.
/// </para>
/// <para>
/// Governance-enabled is the configuration that matters: with governance off, the composite sanitizer
/// binds to the no-op implementation, whose graph is trivially logger-free, so a regression introduced
/// under the real implementation would not show up in the default configuration.
/// </para>
/// </remarks>
public sealed class CompositeResponseSanitizerLoggerCycleTests
{
    [Fact]
    public void GovernanceEnabled_ResolvingTheLoggerFactory_DoesNotReEnterItsOwnFactoryDelegate()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "AppConfig:AI:Governance:Enabled", "true" }
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var completed = false;
        Exception? captured = null;

        // A 1 MB stack, not the default: infinite re-entry should trip this thread's own stack limit
        // quickly rather than consuming the host's memory while the assertion below waits it out.
        var resolution = new Thread(
            () =>
            {
                try
                {
                    _ = provider.GetRequiredService<ILoggerFactory>().CreateLogger("cycle-probe");
                    completed = true;
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            },
            maxStackSize: 1024 * 1024);

        resolution.Start();

        Assert.True(
            resolution.Join(TimeSpan.FromSeconds(20)),
            "Resolving ILoggerFactory did not complete within 20s. Something under ILocalLogRedactor's "
            + "dependency graph now takes an ILogger<T> constructor parameter, re-entering the "
            + "ILoggerFactory factory delegate forever (#457). Use a NullLogger<T> field default there "
            + "instead — see CompositeResponseSanitizer._logger.");
        Assert.True(completed, $"Resolving ILoggerFactory threw: {captured}");
    }
}
