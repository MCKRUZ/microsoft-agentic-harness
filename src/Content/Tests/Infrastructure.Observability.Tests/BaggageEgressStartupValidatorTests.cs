using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System.Diagnostics;
using Xunit;

namespace Infrastructure.Observability.Tests;

/// <summary>
/// Tests for <see cref="BaggageEgressStartupValidator"/>, which refuses to boot a host whose
/// baggage-egress policy (<see cref="Domain.Common.Config.Observability.ObservabilityConfig.PropagateBaggage"/>)
/// has been undone since composition.
/// </summary>
/// <remarks>
/// This check used to live inside <c>Agent365StartupValidator</c>, gated behind that validator's own
/// early return when Agent 365 is disabled — meaning a host with Agent 365 off got none of the
/// protection this policy's own doc comment promises every host (#738's altitude pass). The tests here
/// specifically prove the fix: the check now fires with Agent 365 nowhere in the picture.
/// </remarks>
[Collection(GlobalPropagatorCollection.Name)]
public class BaggageEgressStartupValidatorTests : IDisposable
{
    private readonly TextMapPropagator _originalPropagator =
        Propagators.DefaultTextMapPropagator;

    private readonly System.Diagnostics.DistributedContextPropagator _originalActivityPropagator =
        System.Diagnostics.DistributedContextPropagator.Current;

    /// <summary>
    /// Establishes the baseline every test in this class implicitly relies on: both propagators
    /// trace-context-only, matching what real composition already did before this validator ever
    /// runs. Captured <em>after</em> <see cref="_originalPropagator"/>/<see cref="_originalActivityPropagator"/>,
    /// so <see cref="Dispose"/> still restores whatever this class found the propagators to be, not
    /// this baseline. Individual tests override it deliberately to exercise the case this validator
    /// exists to catch.
    /// </summary>
    public BaggageEgressStartupValidatorTests()
    {
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        System.Diagnostics.DistributedContextPropagator.Current = new TraceContextOnlyActivityPropagator();
    }

    /// <summary>Restores the process-global propagators so a later test never observes this one's swap.</summary>
    public void Dispose()
    {
        Sdk.SetDefaultTextMapPropagator(_originalPropagator);
        System.Diagnostics.DistributedContextPropagator.Current = _originalActivityPropagator;
    }

    private static void UseCompositePropagatorWithBaggage() =>
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));

    private static void UseActivityPropagatorWithBaggage() =>
        System.Diagnostics.DistributedContextPropagator.Current =
            System.Diagnostics.DistributedContextPropagator.CreateDefaultPropagator();

    /// <summary>
    /// A minimal trace-context-only baseline for this suite's constructor. Not the real
    /// <c>BaggageSuppressingDistributedContextPropagator</c> (that type is internal to
    /// Presentation.Common, a different assembly) — just enough to guarantee <c>Fields</c> excludes
    /// "baggage", which is all this validator's check inspects. Measured, not assumed: the BCL's own
    /// <c>DistributedContextPropagator.CreateNoOutputPropagator()</c> was tried first and its
    /// <c>Fields</c> turned out to still list "baggage" despite injecting/extracting nothing.
    /// </summary>
    private sealed class TraceContextOnlyActivityPropagator : System.Diagnostics.DistributedContextPropagator
    {
        public override IReadOnlyCollection<string> Fields { get; } = ["traceparent", "tracestate"];

        public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
        {
        }

        public override void ExtractTraceIdAndState(
            object? carrier,
            PropagatorGetterCallback? getter,
            out string? traceId,
            out string? traceState)
        {
            traceId = null;
            traceState = null;
        }

        public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
            object? carrier,
            PropagatorGetterCallback? getter)
            => null;
    }

    private static BaggageEgressStartupValidator Build(Action<AppConfig>? configure = null)
    {
        var appConfig = new AppConfig();
        configure?.Invoke(appConfig);

        return new BaggageEgressStartupValidator(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig));
    }

    [Fact]
    public async Task DefaultConfig_WithTraceContextOnlyPropagator_DoesNotThrow()
    {
        // The expected steady state, already established by this class's own constructor.
        var validator = Build();

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DefaultConfig_WithACompositePropagatorCarryingBaggage_ThrowsNamingTheCause()
    {
        // Simulates something that ran after AddOpenTelemetry re-registering a composite propagator —
        // a consumer's own Startup code, or a library that calls SetDefaultTextMapPropagator itself.
        // This is the regression the re-assertion exists to catch: without it, this host would boot
        // successfully while silently leaking baggage on every outbound call. No Agent 365 config
        // anywhere in this test — the whole point of moving this check out on its own.
        UseCompositePropagatorWithBaggage();

        var validator = Build();

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("baggage");
        thrown.And.Message.Should().Contain("PropagateBaggage");
    }

    [Fact]
    public async Task PropagateBaggageTrue_WithACompositePropagator_DoesNotThrow()
    {
        // The documented, tested opt-in: a composite propagator here is not a regression to catch,
        // it is precisely what AddOpenTelemetry was configured to install.
        UseCompositePropagatorWithBaggage();

        var validator = Build(c => c.Observability.PropagateBaggage = true);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DefaultConfig_WithActivityPropagatorCarryingBaggage_ThrowsNamingTheCause()
    {
        // A security review of #738 found the original implementation checked only the OpenTelemetry
        // propagator, leaving System.Diagnostics.Activity.Baggage — the store this harness's own
        // identity attribution actually rides via Activity.AddBaggage — completely unguarded. This
        // proves the fix: the Activity-side propagator is re-asserted too, independently of the OTel one.
        UseActivityPropagatorWithBaggage();

        var validator = Build();

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("baggage");
        thrown.And.Message.Should().Contain("PropagateBaggage");
    }

    [Fact]
    public async Task PropagateBaggageTrue_WithAnActivityPropagatorCarryingBaggage_DoesNotThrow()
    {
        // The Activity-side mirror of the documented opt-in: an operator who deliberately set
        // PropagateBaggage=true must not have this validator refuse to boot.
        UseActivityPropagatorWithBaggage();

        var validator = Build(c => c.Observability.PropagateBaggage = true);

        var act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
