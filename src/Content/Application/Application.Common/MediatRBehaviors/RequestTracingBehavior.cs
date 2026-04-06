using System.Diagnostics;
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
    private static readonly ActivitySource Source = new("AgenticHarness.MediatR");

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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message }
            }));
            throw;
        }
    }
}
