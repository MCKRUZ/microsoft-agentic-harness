using System.Diagnostics;
using FluentAssertions;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Tests for <see cref="BaggageSuppressingDistributedContextPropagator"/>, which suppresses the
/// <see cref="Activity.Baggage"/> store a security review of #738 found the original implementation
/// left completely unguarded (only OpenTelemetry's own <c>Baggage</c> API was suppressed).
/// </summary>
public class BaggageSuppressingDistributedContextPropagatorTests
{
    private readonly BaggageSuppressingDistributedContextPropagator _propagator = new();

    [Fact]
    public void Fields_OnlyListsTraceContext()
    {
        // The observable contract BaggageEgressStartupValidator checks: no "baggage" field name means
        // nothing downstream reads this propagator as baggage-carrying.
        _propagator.Fields.Should().BeEquivalentTo(["traceparent", "tracestate"]);
    }

    [Fact]
    public void Inject_WritesTraceContextButOmitsBaggage()
    {
        // This is the exact leak the security review measured: Activity.AddBaggage is how
        // AgUiRunHandler/ConversationOrchestrator/ExecuteAgentTurnCommandHandler publish user and
        // conversation ids. Inject must still carry trace context (distributed tracing keeps working)
        // while dropping baggage and its legacy alias entirely.
        using var activity = new Activity("test-operation").Start();
        activity.AddBaggage("agent.user_id", "victim-user-123");
        var carrier = new Dictionary<string, string>();

        _propagator.Inject(
            activity,
            carrier,
            (object? c, string key, string value) => ((Dictionary<string, string>)c!)[key] = value);

        carrier.Should().ContainKey("traceparent", "distributed tracing must keep working");
        carrier.Should().NotContainKey("baggage", "the store this harness writes identity into must not egress");
        carrier.Should().NotContainKey("Correlation-Context", "the legacy baggage alias must also be suppressed");
    }

    [Fact]
    public void Inject_NullSetter_DoesNotThrow()
    {
        using var activity = new Activity("test-operation").Start();

        var act = () => _propagator.Inject(activity, carrier: null, setter: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ExtractBaggage_AlwaysReturnsNull_EvenWhenCarrierHasABaggageHeader()
    {
        // The inbound half of the leak: a caller-supplied "baggage" header must never be accepted as
        // attacker-chosen attribution. Returning null here is the same "no baggage" result
        // OpenTelemetry's own TraceContextPropagator returns for its half of this policy.
        var carrier = new Dictionary<string, string> { ["baggage"] = "agent.user_id=attacker-chosen" };

        var extracted = _propagator.ExtractBaggage(carrier, Getter(carrier));

        extracted.Should().BeNull();
    }

    [Fact]
    public void ExtractTraceIdAndState_DelegatesToTheRealPropagator()
    {
        // Trace-context extraction is NOT reimplemented here — delegated to the runtime's own default
        // propagator so this type never has to get W3C header parsing right itself. Proved by
        // comparing against that same real propagator's own answer for an identical carrier.
        const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string tracestate = "vendor=value";
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = traceparent,
            ["tracestate"] = tracestate,
        };
        var reference = DistributedContextPropagator.CreateDefaultPropagator();
        reference.ExtractTraceIdAndState(carrier, Getter(carrier), out var expectedTraceId, out var expectedTraceState);

        _propagator.ExtractTraceIdAndState(carrier, Getter(carrier), out var traceId, out var traceState);

        traceId.Should().Be(expectedTraceId);
        traceState.Should().Be(expectedTraceState);
        traceId.Should().Be(traceparent, "the real propagator's extraction behaviour must be preserved");
    }

    private static DistributedContextPropagator.PropagatorGetterCallback Getter(Dictionary<string, string> carrier)
        => (object? c, string fieldName, out string? fieldValue, out IEnumerable<string>? fieldValues) =>
        {
            fieldValues = null;
            fieldValue = ((Dictionary<string, string>)c!).TryGetValue(fieldName, out var value) ? value : null;
        };
}
