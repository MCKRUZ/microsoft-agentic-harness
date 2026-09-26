using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
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

    /// <summary>
    /// Establishes the baseline every test in this class implicitly relies on: a trace-context-only
    /// propagator, matching what real composition already did before this validator ever runs.
    /// Captured <em>after</em> <see cref="_originalPropagator"/>, so <see cref="Dispose"/> still
    /// restores whatever this class found the propagator to be, not this baseline. Individual tests
    /// override it deliberately to exercise the case this validator exists to catch.
    /// </summary>
    public BaggageEgressStartupValidatorTests() =>
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

    /// <summary>Restores the process-global propagator so a later test never observes this one's swap.</summary>
    public void Dispose() => Sdk.SetDefaultTextMapPropagator(_originalPropagator);

    private static void UseCompositePropagatorWithBaggage() =>
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));

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
}
