using Domain.Common.Config.Observability;
using System.Diagnostics;

namespace Application.Common.Services.Telemetry;

/// <summary>
/// A <see cref="DistributedContextPropagator"/> that carries trace context (<c>traceparent</c>/
/// <c>tracestate</c>) exactly as the runtime default does, but never injects or extracts baggage.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ObservabilityConfig.PropagateBaggage"/> (#738) was originally implemented by setting
/// only <c>OpenTelemetry.Sdk.SetDefaultTextMapPropagator</c> — but that governs a different
/// baggage store than the one this harness actually writes identity into. OpenTelemetry's own
/// <c>Baggage</c> API (what <c>BaggageBuilder</c> writes) and <see cref="Activity.Baggage"/> (what
/// <c>AddBaggage</c> writes — see <c>AgUiRunHandler</c>, <c>ConversationOrchestrator</c>,
/// <c>ExecuteAgentTurnCommandHandler</c>, <c>AgentExecutionContextFactory</c>) are separate stores
/// with separate propagators: the first is <c>OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator</c>,
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
/// Lives here, in <c>Application.Common</c>, after THREE placements: first written in
/// <c>Presentation.Common</c>, moved to <c>Infrastructure.Observability</c> for co-location with its
/// sibling <c>BaggageEgressStartupValidator</c>, then moved to <c>Application.AI.Common</c> when a
/// code-review round found that Infrastructure placement's own justification misstated what this
/// repo's <c>clean-architecture.md</c> litmus test actually says. A THIRD altitude pass found
/// <c>Application.AI.Common</c> was still wrong: this type has zero AI-agent semantics — it is generic
/// ASP.NET Core/HttpClient distributed-tracing baggage suppression, and every one of that project's
/// other 100+ files is scoped to AI-agent-specific concerns. <c>Application.Common</c> already hosts
/// this repo's non-AI telemetry seam (<c>Interfaces/Telemetry/ITelemetryConfigurator</c>) and has its
/// own <c>Services/</c> folder for concrete, non-AI service implementations — the actual correct home
/// by subject matter, not merely by "which project reference direction compiles." Only the
/// composition-root registration call in
/// <c>OpenTelemetryServiceCollectionExtensions.AddOpenTelemetry</c> (Presentation.Common) is
/// legitimately Presentation work; <c>BaggageEgressStartupValidator</c> (Infrastructure.Observability)
/// re-checks this type's effect via <see cref="DistributedContextPropagator.Current"/>'s
/// <c>Fields</c> without ever referencing this type directly.
/// </para>
/// <para>
/// Field-name detection has the same limit as the OpenTelemetry-side check this type parallels:
/// it recognises the runtime's own <c>baggage</c>/<c>Correlation-Context</c> header names, not a
/// differently-named baggage-carrying propagator a consumer might register (a Jaeger-style
/// <c>uberctx-*</c> propagator, for example) — see <c>Infrastructure.Observability.BaggageEgressStartupValidator</c>'s
/// own remarks for the equivalent OTel-side caveat.
/// </para>
/// </remarks>
public sealed class BaggageSuppressingDistributedContextPropagator : DistributedContextPropagator
{
    // Case-insensitive: HTTP header names are case-insensitive by spec, and this set is the security
    // boundary — a propagator emitting "Baggage" instead of "baggage" would be spec-legal and would
    // walk straight through an ordinal comparison. Not live against the pinned runtime's W3CPropagator
    // (measured: it emits lowercase), but the filter should not depend on a casing detail of a type
    // this codebase doesn't own — found by a security review of #738.
    private static readonly HashSet<string> SuppressedFieldNames =
        new(StringComparer.OrdinalIgnoreCase) { "baggage", "Correlation-Context" };

    // A static, no-capture delegate — allocated once per process, not once per Inject call. Unpacks the
    // per-call carrier/setter pair from the InjectState the real propagator is handed as its own
    // "carrier" argument, so the only per-call allocation left is that one small state object, not a
    // closure-plus-delegate pair. DistributedContextPropagator.Current.Inject runs on every outbound
    // HttpClient call once this propagator is installed, so this is a genuine hot path — found by
    // #738's own efficiency review.
    private static readonly PropagatorSetterCallback FilteringSetter = (state, key, value) =>
    {
        if (SuppressedFieldNames.Contains(key))
        {
            return;
        }

        var injectState = (InjectState)state!;
        injectState.Setter(injectState.Carrier, key, value);
    };

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

        _inner.Inject(activity, new InjectState(carrier, setter), FilteringSetter);
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

    /// <summary>Carries one <see cref="Inject"/> call's real carrier and setter through <see cref="_inner"/>.</summary>
    private sealed class InjectState(object? carrier, PropagatorSetterCallback setter)
    {
        public object? Carrier { get; } = carrier;

        public PropagatorSetterCallback Setter { get; } = setter;
    }
}
