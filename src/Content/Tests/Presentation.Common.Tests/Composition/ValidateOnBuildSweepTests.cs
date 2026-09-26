using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Self-maintaining guard for audit item H2: the production composition root must
/// pass <c>ValidateOnBuild = true</c>, meaning EVERY registered service — including
/// every MediatR handler discovered by assembly scanning — can actually be constructed.
/// </summary>
/// <remarks>
/// <para>
/// Before this guard, four globally-scanned handlers depended on interfaces supplied by
/// only one host (AgentHub's SignalR notifiers) or one opt-in subsystem (the eval runner,
/// the prompt-usage store). Any other host registered the handler but could not build it —
/// a latent runtime crash the moment that command was dispatched. <c>ValidateOnBuild</c>
/// converts that silent-until-dispatched failure into a loud boot failure, and this test
/// converts it further into a caught-at-CI failure.
/// </para>
/// <para>
/// The fix is a No-op / not-configured default for each such dependency (mirroring the
/// existing <c>NullEvalRunNotifier</c> pattern), so the graph is constructible in every
/// host; the real host-specific implementation still wins via last-registration-wins.
/// If a future change adds a handler whose dependency has no default, this test fails with
/// the exact unresolved service — not a customer's production stack trace.
/// </para>
/// <para>
/// #251's original ~106 failures came nearly all from conditionally-registered handlers
/// whose dependencies are only present under some configurations (e.g., handlers gated by
/// Governance.Enabled). Testing only the all-features-off baseline is blind to that class.
/// This test is parameterized to cover both the baseline and representative features-on
/// configurations, closing the gap that #251's first fix left.
/// </para>
/// </remarks>
[Collection(GlobalPropagatorCollection.Name)]
public sealed class ValidateOnBuildSweepTests : IDisposable
{
    // Every fact in this class builds a real composition root, and AddOpenTelemetry unconditionally
    // sets BOTH process-global default propagators as part of that (#738: the OpenTelemetry one AND
    // System.Diagnostics.DistributedContextPropagator.Current) — not only the Agent 365-enabled fact,
    // which is the only one this used to guard. Captured once per test instance (xUnit creates a
    // fresh instance per [Fact]) and restored in Dispose, or this class leaks a process-global
    // mutation into whatever test — in this collection or, once test-process scheduling reorders
    // across the assembly, another — runs next.
    private readonly OpenTelemetry.Context.Propagation.TextMapPropagator _originalPropagator =
        OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;

    private readonly System.Diagnostics.DistributedContextPropagator _originalActivityPropagator =
        System.Diagnostics.DistributedContextPropagator.Current;

    /// <inheritdoc />
    public void Dispose()
    {
        OpenTelemetry.Sdk.SetDefaultTextMapPropagator(_originalPropagator);
        System.Diagnostics.DistributedContextPropagator.Current = _originalActivityPropagator;
    }

    /// <summary>
    /// All-features-off baseline: the default, all-features-off registration set every host
    /// shares before its host-specific overrides. This is the baseline that must always
    /// be constructible.
    /// </summary>
    [Fact]
    public void ProductionCompositionRoot_AllFeaturesOff_BuildsWithValidateOnBuild()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using var provider = BuildAndValidate(configuration).Provider;
    }

    /// <summary>
    /// Governance-enabled configuration: handlers that depend on governance services are only
    /// registered when <c>AppConfig:AI:Governance:Enabled = true</c>. Validates that the graph
    /// is constructible when this feature is on.
    /// </summary>
    [Fact]
    public void ProductionCompositionRoot_GovernanceEnabled_BuildsWithValidateOnBuild()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "AppConfig:AI:Governance:Enabled", "true" }
            })
            .Build();

        using var provider = BuildAndValidate(configuration).Provider;
    }

    /// <summary>
    /// Agent 365-enabled configuration: the exporter's own services are registered by the vendor
    /// distro only when the feature is on, so neither of the facts above ever constructs them.
    /// Validates that the graph is still constructible with agent-activity export enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ids are arbitrary but must be GUIDs: <c>Agent365ExporterConfigValidator</c> runs on start
    /// and refuses a non-GUID agent or tenant id, so a placeholder string here would fail this test
    /// for the wrong reason.
    /// </para>
    /// <para>
    /// <strong>The <c>WebTelemetryProjects</c> entry is what makes this test do anything.</strong>
    /// <c>AddOpenTelemetry</c> branches on whether the entry assembly is listed there, and in a test
    /// process the entry assembly is the test host — so without this the composition takes the
    /// standalone path, never reaches the Agent 365 wiring, and the vendor pipeline this test exists to
    /// construct is never built. The first version of this test omitted it and passed vacuously.
    /// </para>
    /// <para>
    /// <strong>Non-vacuity proof, third version (#738's own code-review round 2).</strong> The first
    /// version used "baggage absent from the default propagator" — invalidated the moment baggage
    /// suppression became a host-wide policy applied in <c>AddOpenTelemetry</c> regardless of Agent 365.
    /// The second version resolved <c>IAgentTelemetryAttribution</c> and asserted its concrete type —
    /// also vacuous, found by the SAME review round: <c>Infrastructure.Observability</c>'s own
    /// <c>DependencyInjection.cs</c> registers <c>Agent365TelemetryAttribution</c> with a plain,
    /// unconditional <c>AddSingleton</c>, independent of <c>Agent365:Enabled</c> — so that assertion
    /// would have passed identically with Agent 365 entirely disabled. This version instead checks the
    /// raw <c>IServiceCollection</c> for a descriptor whose type name contains "Agent365" (the same
    /// technique <c>Agent365ExporterWiringTests.IsAgent365Service</c> uses) — a signal only the vendor's
    /// own conditionally-registered pipeline, not anything this repo's own DI modules register
    /// unconditionally, can produce.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProductionCompositionRoot_Agent365Enabled_BuildsWithValidateOnBuild()
    {
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
            ?? "UnknownService";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "AppConfig:Observability:WebTelemetryProjects:0", entryAssembly },
                { "AppConfig:Observability:Exporters:Agent365:Enabled", "true" },
                {
                    "AppConfig:Observability:Exporters:Agent365:AgentAppId",
                    "11111111-1111-1111-1111-111111111111"
                },
                {
                    "AppConfig:Observability:Exporters:Agent365:TenantId",
                    "22222222-2222-2222-2222-222222222222"
                },
            })
            .Build();

        var (provider, services) = BuildAndValidate(configuration);
        using var _ = provider;

        services.Should().Contain(
            d => IsAgent365Service(d),
            "enabling Agent 365 must compose the vendor's own Agent 365 pipeline — a signal this repo's "
            + "own unconditionally-registered defaults cannot produce");
    }

    // Matched by name rather than by CLR type on purpose: the vendor's Agent 365 service types are
    // internal to its assembly, so a typed reference will not compile — mirrors
    // Agent365ExporterWiringTests.IsAgent365Service exactly. A method call rather than an inlined
    // null-conditional expression, because FluentAssertions' Contain(Expression&lt;Func&lt;T, bool&gt;&gt;)
    // overload cannot capture a `?.` operator in an expression tree.
    private static bool IsAgent365Service(ServiceDescriptor descriptor)
        => (descriptor.ServiceType.FullName ?? string.Empty).Contains("Agent365", StringComparison.Ordinal)
            || (descriptor.ImplementationType?.FullName ?? string.Empty).Contains("Agent365", StringComparison.Ordinal);

    /// <summary>
    /// Builds and validates the composition root, returning the built provider AND the registration
    /// set that produced it — the latter lets a caller inspect what actually got wired (see
    /// <see cref="ProductionCompositionRoot_Agent365Enabled_BuildsWithValidateOnBuild"/>) without
    /// re-registering everything a second time. The caller owns the provider's disposal.
    /// </summary>
    private static (ServiceProvider Provider, IServiceCollection Services) BuildAndValidate(
        IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        // ValidateOnBuild eagerly constructs every non-open-generic descriptor and throws an
        // AggregateException listing ALL that cannot be built. ValidateScopes is kept on to
        // match the production hosts (captive-dependency guard, audit item H2's sibling).
        ServiceProvider? provider = null;
        var exception = Record.Exception(() =>
        {
            provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        });

        Assert.Null(exception);
        return (provider!, services);
    }
}
