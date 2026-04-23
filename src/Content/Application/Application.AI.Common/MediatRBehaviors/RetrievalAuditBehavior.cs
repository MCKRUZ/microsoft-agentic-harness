using System.Diagnostics;
using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Pipeline behavior that records retrieval audit telemetry for requests
/// implementing <see cref="IRetrievalAuditable"/>. Captures the query text,
/// handler execution duration, and emits OpenTelemetry Activity tags for
/// downstream observability (dashboards, alerting, query analysis).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline position: runs after content safety and validation behaviors.
/// For non-<see cref="IRetrievalAuditable"/> requests, passes through
/// to the next handler with zero overhead.
/// </para>
/// <para>
/// This behavior records timing and query metadata only. Classification
/// and transformation tags are set by the <c>QueryRouter</c> in the
/// Infrastructure layer — this behavior captures the outer envelope
/// (total retrieval duration, query text hash) that the router cannot see.
/// </para>
/// </remarks>
public sealed class RetrievalAuditBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.RetrievalAudit");

    private readonly ILogger<RetrievalAuditBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetrievalAuditBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">Logger for recording retrieval audit events.</param>
    public RetrievalAuditBehavior(ILogger<RetrievalAuditBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IRetrievalAuditable auditable)
            return await next();

        using var activity = ActivitySource.StartActivity("rag.retrieval_audit");
        activity?.SetTag("rag.query.text_length", auditable.QueryText.Length);

        var stopwatch = Stopwatch.StartNew();

        var response = await next();

        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        activity?.SetTag(RagConventions.RetrievalLatencyMs, elapsedMs);

        _logger.LogInformation(
            "Retrieval audit: query ({QueryLength} chars) completed in {ElapsedMs:F1}ms",
            auditable.QueryText.Length, elapsedMs);

        return response;
    }
}
