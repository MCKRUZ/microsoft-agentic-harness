using Application.Common.Services.Telemetry;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        // The real propagator, not a hand-rolled stand-in — Infrastructure depending on Application is
        // the normal direction (BaggageSuppressingDistributedContextPropagator lives in
        // Application.Common, its third and final placement this PR), so there is no assembly-boundary
        // reason to reimplement its "trace-context-only" baseline here.
        System.Diagnostics.DistributedContextPropagator.Current = new BaggageSuppressingDistributedContextPropagator();
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

    private static BaggageEgressStartupValidator Build(
        Action<AppConfig>? configure = null,
        ILogger<BaggageEgressStartupValidator>? logger = null,
        System.Diagnostics.DistributedContextPropagator? resolvedPropagator = null)
    {
        var appConfig = new AppConfig();
        configure?.Invoke(appConfig);

        return new BaggageEgressStartupValidator(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            logger ?? NullLogger<BaggageEgressStartupValidator>.Instance,
            // Defaults to whatever Current is right now, matching what production keeps in sync via
            // AddOpenTelemetry's DI Replace() call — a test that wants to prove the DI-resolved check
            // is genuinely independent of Current passes a deliberately different instance.
            resolvedPropagator ?? System.Diagnostics.DistributedContextPropagator.Current);
    }

    /// <summary>
    /// Verifies a warning was logged. With <paramref name="contains"/> omitted, matches any warning at
    /// all — too loose to distinguish the general warning from the Agent-365-additive one, which is
    /// exactly why every call site below now passes it. A security review of #738 found the omitted
    /// form let a test named "...LogsAWarningNamingTenantAndAgent" pass with the entire Agent-365
    /// branch deleted (mutation-proven): the assertion never actually inspected the rendered message.
    /// </summary>
    private static void VerifyWarningLogged(
        Mock<ILogger<BaggageEgressStartupValidator>> logger, Times times, string? contains = null)
        => logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    contains == null || (v!.ToString() ?? string.Empty).Contains(contains, StringComparison.Ordinal)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

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
    public async Task DefaultConfig_WithOnlyTheDiResolvedPropagatorCarryingBaggage_ThrowsNamingTheCause()
    {
        // CI's correctness-review and security-review gates found that DistributedContextPropagator.Current
        // and the instance ASP.NET Core actually resolves from DI can drift on a web host — the static
        // property alone was not what the inbound request pipeline used. This proves the two checks are
        // genuinely independent, not the same check labelled twice: Current stays trace-context-only
        // (the constructor baseline) while the DI-resolved instance is deliberately different and does
        // carry baggage — the validator must still refuse to boot.
        var validator = Build(resolvedPropagator: System.Diagnostics.DistributedContextPropagator.CreateDefaultPropagator());

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("baggage");
        thrown.And.Message.Should().Contain("resolved from DI");
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

    [Fact]
    public async Task DefaultConfig_WithAPropagatorCarryingOnlyTheLegacyCorrelationContextAlias_ThrowsNamingTheCause()
    {
        // A second code-review round on #738 found the field-name check recognised "baggage" but not
        // its legacy "Correlation-Context" alias — even though the propagator this class suppresses
        // treats both as equally sensitive. A propagator whose Fields carry ONLY the legacy name (no
        // literal "baggage") would otherwise pass this check while still egressing identity via that
        // header. Proved here with a minimal propagator that reports only the legacy name.
        System.Diagnostics.DistributedContextPropagator.Current = new CorrelationContextOnlyPropagator();

        var validator = Build();

        var act = () => validator.StartAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("PropagateBaggage");
    }

    /// <summary>A propagator whose Fields report only "Correlation-Context", never the literal "baggage".</summary>
    private sealed class CorrelationContextOnlyPropagator : System.Diagnostics.DistributedContextPropagator
    {
        public override IReadOnlyCollection<string> Fields { get; } = ["traceparent", "Correlation-Context"];

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

    // --- The opted-in warning (moved here from Agent365StartupValidator by #738's second altitude pass) --

    [Fact]
    public async Task PropagateBaggageTrue_LogsAWarning_EvenWithNoAgent365Config()
    {
        // The general case this warning exists for: this harness's own identity attribution
        // (AgUiRunHandler, ConversationOrchestrator, ExecuteAgentTurnCommandHandler) writes to Activity
        // baggage with zero Agent 365 dependency, so the warning must fire on the flag alone — nesting
        // it behind Agent 365's own Enabled flag (the earlier cut) left every non-Agent-365 host silent.
        var logger = new Mock<ILogger<BaggageEgressStartupValidator>>();
        var validator = Build(c => c.Observability.PropagateBaggage = true, logger.Object);

        await validator.StartAsync(CancellationToken.None);

        VerifyWarningLogged(logger, Times.Once());
        // Pins this test to the GENERAL branch specifically — a security review found the untargeted
        // assertion above let this test pass with the Agent-365-additive branch's condition mutated to
        // dead code (mutation-proven), since it never inspected the rendered message.
        VerifyWarningLogged(logger, Times.Never(), "Agent 365 export is ALSO enabled");
    }

    [Fact]
    public async Task PropagateBaggageTrueWithAgent365Enabled_LogsAWarningNamingTenantAndAgent()
    {
        // The additive case: Agent 365's own attribution rides the same egress, so the warning names it
        // specifically when that exporter is also enabled — but Agent 365 is a detail, not the gate.
        var logger = new Mock<ILogger<BaggageEgressStartupValidator>>();
        var validator = Build(
            c =>
            {
                c.Observability.PropagateBaggage = true;
                c.Observability.Exporters.Agent365.Enabled = true;
                c.Observability.Exporters.Agent365.AgentAppId = "11111111-1111-1111-1111-111111111111";
                c.Observability.Exporters.Agent365.TenantId = "22222222-2222-2222-2222-222222222222";
            },
            logger.Object);

        await validator.StartAsync(CancellationToken.None);

        // Asserts on the actual rendered message, not just "a warning fired" — a security review found
        // the untargeted form let this test pass with the entire Agent-365 branch deleted.
        VerifyWarningLogged(logger, Times.Once(), "Agent 365 export is ALSO enabled");
    }

    [Fact]
    public async Task PropagateBaggageFalse_LogsNoWarning()
    {
        // The steady state — nothing to warn about when baggage is suppressed as expected.
        var logger = new Mock<ILogger<BaggageEgressStartupValidator>>();
        var validator = Build(logger: logger.Object);

        await validator.StartAsync(CancellationToken.None);

        VerifyWarningLogged(logger, Times.Never());
    }
}
