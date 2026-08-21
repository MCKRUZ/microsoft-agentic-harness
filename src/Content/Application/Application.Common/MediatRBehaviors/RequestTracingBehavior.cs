using System.Diagnostics;
using Domain.Common.Telemetry;
using MediatR;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Pipeline behavior that wraps each request in an OpenTelemetry
/// <see cref="Activity"/> span. Replaces <c>RequestPerformanceBehavior</c>
/// (span duration is the timing). Works alongside <c>UnhandledExceptionBehavior</c>
/// which handles structured logging with agent context enrichment.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: 8 (inner). <c>UnhandledExceptionBehavior</c> is outermost (position 1).
/// All other behaviors execute within this span,
/// giving you end-to-end timing, exception recording, and custom tags in a single
/// trace without redundant Stopwatch or try/catch behaviors.
/// </para>
/// <para>
/// Slow request detection moves to the OTel backend (Jaeger, Azure Monitor) via
/// alerting on span duration — configurable without code changes.
/// </para>
/// </remarks>
public sealed class RequestTracingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly ActivitySource Source = new(AppSourceNames.AgenticHarnessMediatR);

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestType = typeof(TRequest);
        using var activity = Source.StartActivity(requestType.Name);
        activity?.SetTag("mediatr.request_type", requestType.FullName);

        try
        {
            var response = await next();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex)
        {
            // Type name only, never ex.Message — this behavior wraps every command/query in the
            // app, so a request handler that throws with a secret in its message (connection
            // string, SAS token) must not have that message land in the exported span. Matches
            // the convention MediatorDispatchRunner/WorkspaceCommandRunner already use; this file
            // (Application.Common) has no reachable IContentRedactionFilter — that interface lives
            // in Application.AI.Common, a layer above this one — so pattern-based redaction isn't
            // an option here without moving the interface, which is a bigger architectural change
            // than this fix.
            var typeName = ex.GetType().Name;
            activity?.SetStatus(ActivityStatusCode.Error, typeName);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", typeName }
            }));
            throw;
        }
    }
}
