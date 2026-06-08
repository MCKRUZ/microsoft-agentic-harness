using System.Diagnostics;
using Domain.AI.A2A;
using Domain.AI.Telemetry.Conventions;

namespace Infrastructure.AI.A2A;

/// <summary>
/// Owns the OTel <see cref="ActivitySource"/> for the A2A surface and stamps
/// caller/callee/correlation attributes on both the client-side
/// (<c>a2a.client {callee}</c>) and server-side (<c>a2a.server {skill}</c>)
/// spans.
/// </summary>
/// <remarks>
/// <para>
/// Single source per process: <see cref="A2AConventions.ActivitySourceName"/>.
/// Presentation registers this name with the OTel tracer provider.
/// </para>
/// <para>
/// Span kind is <see cref="ActivityKind.Client"/> on the caller side and
/// <see cref="ActivityKind.Server"/> on the callee side so downstream
/// processors can tier-sample on the two surfaces independently.
/// </para>
/// </remarks>
public sealed class A2ASpanEmitter : IDisposable
{
    private readonly ActivitySource _source;

    /// <summary>
    /// Creates an emitter with an <see cref="ActivitySource"/> named
    /// <see cref="A2AConventions.ActivitySourceName"/>.
    /// </summary>
    public A2ASpanEmitter()
    {
        _source = new ActivitySource(A2AConventions.ActivitySourceName);
    }

    /// <summary>
    /// Starts the caller-side <c>a2a.client {callee_agent_id}</c> span and
    /// stamps caller/callee/correlation/transport/auth attributes.
    /// </summary>
    public Activity? StartClientSpan(
        A2AEnvelope envelope,
        string transport,
        string authScheme)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        var activity = _source.StartActivity(
            $"{A2AConventions.SpanNameClientPrefix}{envelope.CalleeAgentId}",
            ActivityKind.Client);
        if (activity is null) return null;

        StampCommonAttributes(activity, envelope, transport, authScheme);
        return activity;
    }

    /// <summary>
    /// Starts the callee-side <c>a2a.server {callee_skill}</c> span as a child
    /// of the propagated trace context.
    /// </summary>
    public Activity? StartServerSpan(
        A2AEnvelope envelope,
        string transport,
        string authScheme)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        var spanName = envelope.CalleeSkill is null
            ? $"{A2AConventions.SpanNameServerPrefix}{envelope.CalleeAgentId}"
            : $"{A2AConventions.SpanNameServerPrefix}{envelope.CalleeSkill}";

        var activity = _source.StartActivity(spanName, ActivityKind.Server);
        if (activity is null) return null;

        StampCommonAttributes(activity, envelope, transport, authScheme);
        return activity;
    }

    /// <summary>
    /// Closes a span with an error status when the call failed. The error code
    /// is also stamped as <c>gen_ai.a2a.error.code</c> so dashboards can
    /// alert on specific failure modes.
    /// </summary>
    public static void EndWithError(Activity? span, string errorCode, string? errorMessage)
    {
        if (span is null) return;
        span.SetTag(A2AConventions.ErrorCode, errorCode);
        span.SetTag(GenAiSemconvRegistry.ErrorType, errorCode);
        span.SetStatus(ActivityStatusCode.Error, errorMessage);
        span.Dispose();
    }

    /// <summary>Disposes the underlying <see cref="ActivitySource"/>.</summary>
    public void Dispose() => _source.Dispose();

    private static void StampCommonAttributes(
        Activity activity,
        A2AEnvelope envelope,
        string transport,
        string authScheme)
    {
        activity.SetTag(GenAiSemconvRegistry.OperationName, A2AConventions.OperationInvokeA2A);
        activity.SetTag(A2AConventions.CorrelationId, envelope.CorrelationId);
        activity.SetTag(A2AConventions.CallerId, envelope.CallerAgentId);
        activity.SetTag(A2AConventions.CallerKind, envelope.CallerKind);
        activity.SetTag(A2AConventions.CalleeId, envelope.CalleeAgentId);
        if (envelope.CalleeSkill is not null)
        {
            activity.SetTag(A2AConventions.CalleeSkill, envelope.CalleeSkill);
        }
        activity.SetTag(A2AConventions.Transport, transport);
        activity.SetTag(A2AConventions.AuthScheme, authScheme);
        activity.SetTag(A2AConventions.EnvelopeVersion, envelope.SchemaVersion);
    }
}
