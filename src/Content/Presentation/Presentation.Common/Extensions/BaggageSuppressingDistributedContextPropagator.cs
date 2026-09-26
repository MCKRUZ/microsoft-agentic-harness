using Domain.Common.Config.Observability;
using System.Diagnostics;

namespace Presentation.Common.Extensions;

/// <summary>
/// A <see cref="DistributedContextPropagator"/> that carries trace context (<c>traceparent</c>/
/// <c>tracestate</c>) exactly as the runtime default does, but never injects or extracts baggage.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ObservabilityConfig.PropagateBaggage"/> (#738) was originally implemented by setting
/// only <see cref="OpenTelemetry.Sdk.SetDefaultTextMapPropagator"/> — but that governs a different
/// baggage store than the one this harness actually writes identity into. OpenTelemetry's own
/// <c>Baggage</c> API (what <c>BaggageBuilder</c> writes) and <see cref="Activity.Baggage"/> (what
/// <c>AddBaggage</c> writes — see <c>AgUiRunHandler</c>, <c>ConversationOrchestrator</c>,
/// <c>ExecuteAgentTurnCommandHandler</c>, <c>AgentExecutionContextFactory</c>) are separate stores
/// with separate propagators: the first is <see cref="OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator"/>,
/// the second is <see cref="DistributedContextPropagator.Current"/>. Every user id and conversation
/// id this harness publishes rides the second one. Measured against the pinned runtime
/// (.NET 10.0.12): the default <see cref="DistributedContextPropagator.Current"/> is
/// <c>W3CPropagator</c>, whose <c>Fields</c> include <c>baggage</c> and <c>Correlation-Context</c>
/// alongside <c>traceparent</c>/<c>tracestate</c>, and it both injects <see cref="Activity.Baggage"/>
/// onto outbound <c>HttpClient</c> calls and extracts a caller-supplied <c>baggage</c> header into
/// inbound <see cref="Activity"/> instances via ASP.NET Core's own diagnostics — with no OpenTelemetry
/// component anywhere in the pipeline that touches it. Setting only the OTel propagator left this
/// leak (and the matching inbound spoofing surface) fully open regardless of
/// <see cref="ObservabilityConfig.PropagateBaggage"/>'s value; this type closes it.
/// </para>
/// <para>
/// Delegates trace-id/state extraction to the runtime's own default propagator rather than
/// reimplementing W3C header parsing — that parsing is exactly the kind of wire-format logic this
/// codebase has gotten wrong from first principles before (see the three-round narrowing history on
/// <c>ToolResultText.TryGetContentArray</c> in this repo's Common Mistakes). Only the two baggage
/// operations are overridden: <see cref="Inject"/> filters the <c>baggage</c>/<c>Correlation-Context</c>
/// keys out of what the real propagator would have written, and <see cref="ExtractBaggage"/>
/// unconditionally returns <c>null</c> — the same "no baggage" result OpenTelemetry's own
/// <c>TraceContextPropagator</c> returns for its half of this policy.
/// </para>
/// <para>
/// Field-name detection has the same limit as the OpenTelemetry-side check this type parallels:
/// it recognises the runtime's own <c>baggage</c>/<c>Correlation-Context</c> header names, not a
/// differently-named baggage-carrying propagator a consumer might register (a Jaeger-style
/// <c>uberctx-*</c> propagator, for example) — see <see cref="Infrastructure.Observability.BaggageEgressStartupValidator"/>'s
/// own remarks for the equivalent OTel-side caveat.
/// </para>
/// </remarks>
internal sealed class BaggageSuppressingDistributedContextPropagator : DistributedContextPropagator
{
    private static readonly string[] SuppressedFieldNames = ["baggage", "Correlation-Context"];

    private readonly DistributedContextPropagator _inner = CreateDefaultPropagator();

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Fields { get; } = ["traceparent", "tracestate"];

    /// <inheritdoc />
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null)
        {
            return;
        }

        _inner.Inject(activity, carrier, (innerCarrier, key, value) =>
        {
            if (Array.IndexOf(SuppressedFieldNames, key) >= 0)
            {
                return;
            }

            setter(innerCarrier, key, value);
        });
    }

    /// <inheritdoc />
    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
        => _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    /// <inheritdoc />
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter)
        => null;
}
