using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Span processor that enriches <c>execute_tool</c> spans with effectiveness
/// attributes (result size, empty detection, truncation) and records tool
/// execution metrics. Inspired by Nexus tool usefulness scoring which
/// measures result substance, chain detection, and reference tracking.
/// </summary>
/// <remarks>
/// <para>
/// Rather than computing a composite "usefulness score" (which requires
/// message-level analysis), this processor captures the raw dimensions
/// that backends can aggregate into effectiveness dashboards:
/// </para>
/// <list type="bullet">
///   <item><description>Result emptiness — tool returned nothing useful</description></item>
///   <item><description>Result size — bytes of output produced</description></item>
///   <item><description>Result truncation — output exceeded max length</description></item>
///   <item><description>Duration and error rate — from existing <see cref="ToolExecutionMetrics"/></description></item>
/// </list>
/// </remarks>
public sealed class ToolEffectivenessProcessor : BaseProcessor<Activity>
{

    private readonly ILogger<ToolEffectivenessProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolEffectivenessProcessor"/> class.
    /// </summary>
    public ToolEffectivenessProcessor(ILogger<ToolEffectivenessProcessor> logger)
    {
        _logger = logger;
        _logger.LogInformation("Tool effectiveness processor initialized");
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        var opName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
        if (!string.Equals(opName, ToolConventions.ExecuteToolOperation, StringComparison.Ordinal))
            return;

        var toolName = data.GetTagItem(ToolConventions.Name) as string ?? "unknown";
        var result = data.GetTagItem(ToolConventions.ToolCallResult) as string;

        // Enrich span with effectiveness attributes
        var isEmpty = string.IsNullOrWhiteSpace(result);
        var resultChars = result?.Length ?? 0;
        var isTruncated = resultChars > ToolConventions.MaxResultLength;

        data.SetTag(ToolConventions.ResultEmpty, isEmpty);
        data.SetTag(ToolConventions.ResultChars, resultChars);
        data.SetTag(ToolConventions.ResultTruncated, isTruncated);

        // Record metrics
        var tags = new TagList { { ToolConventions.Name, toolName } };

        ToolExecutionMetrics.Duration.Record(data.Duration.TotalMilliseconds, tags);

        var status = data.Status == ActivityStatusCode.Error
            ? ToolConventions.StatusValues.Failure
            : ToolConventions.StatusValues.Success;
        var statusTags = new TagList
        {
            { ToolConventions.Name, toolName },
            { ToolConventions.Status, status }
        };
        ToolExecutionMetrics.Invocations.Add(1, statusTags);

        if (isEmpty)
        {
            ToolExecutionMetrics.EmptyResults.Add(1, tags);
        }

        ToolExecutionMetrics.ResultSize.Record(resultChars, tags);

        if (data.Status == ActivityStatusCode.Error)
        {
            var errorType = data.GetTagItem(ToolConventions.ErrorType) as string ?? "unknown";
            ToolExecutionMetrics.Errors.Add(1, new TagList
            {
                { ToolConventions.Name, toolName },
                { ToolConventions.ErrorType, errorType }
            });
        }
    }
}
