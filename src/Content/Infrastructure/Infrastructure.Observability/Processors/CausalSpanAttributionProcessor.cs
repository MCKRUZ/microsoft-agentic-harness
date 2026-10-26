using System.Diagnostics;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Span processor that enriches <c>execute_tool</c> spans with causal attribution
/// attributes following the OTel GenAI semantic conventions.
/// </summary>
/// <remarks>
/// <para>Runs after <see cref="ToolEffectivenessProcessor"/> in the pipeline.</para>
/// <para>Attributes added to tool spans:</para>
/// <list type="bullet">
///   <item><description><c>gen_ai.tool.name</c> — bridged from <c>agent.tool.name</c></description></item>
///   <item><description><c>tool.input_hash</c> — keyed fingerprint of the serialized tool input arguments from <see cref="ITelemetryPseudonymizer.Fingerprint"/> (only when <c>IsAllDataRequested</c>); keyed, because an unkeyed hash of short arguments can be reversed by hashing guesses</description></item>
///   <item><description><c>tool.result_category</c> — bucketed outcome from span status</description></item>
///   <item><description><c>gen_ai.harness.candidate_id</c> — from Activity baggage when in an eval context</description></item>
///   <item><description><c>gen_ai.harness.iteration</c> — from Activity baggage when in an eval context</description></item>
/// </list>
/// </remarks>
public sealed class CausalSpanAttributionProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<CausalSpanAttributionProcessor> _logger;
    private readonly ITelemetryPseudonymizer _pseudonymizer;

    /// <summary>Initializes a new instance of <see cref="CausalSpanAttributionProcessor"/>.</summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="pseudonymizer">The shared keyed hash the tool-argument fingerprint is made with.</param>
    public CausalSpanAttributionProcessor(
        ILogger<CausalSpanAttributionProcessor> logger,
        ITelemetryPseudonymizer pseudonymizer)
    {
        _logger = logger;
        _pseudonymizer = pseudonymizer;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // Only process execute_tool spans
        var operationName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
        if (!string.Equals(operationName, ToolConventions.ExecuteToolOperation, StringComparison.Ordinal))
            return;

        // Bridge agent.tool.name → gen_ai.tool.name (OTel GenAI semantic convention)
        var toolName = data.GetTagItem(ToolConventions.Name) as string;
        if (toolName is not null)
            data.SetTag(ToolConventions.GenAiToolName, toolName);

        // Input fingerprint — keyed hash of the tool arguments. Only when full data is requested (performance guard).
        // Note: PiiFilteringProcessor runs at pipeline position 1 (before this processor at position 5),
        // so ToolCallArguments has already been scrubbed of sensitive values before we hash it.
        if (data.IsAllDataRequested)
        {
            var inputValue = data.GetTagItem(ToolConventions.ToolCallArguments) as string ?? string.Empty;
            data.SetTag(
                ToolConventions.InputHash,
                _pseudonymizer.Fingerprint(ITelemetryPseudonymizer.Purposes.ToolArguments, inputValue));
        }

        // Result category from span status or existing tag.
        // Span-level categories are intentionally narrower than ExecutionTraceRecord categories:
        // spans map only to "success" or "error" via ActivityStatusCode. The "partial", "timeout",
        // and "blocked" values are set directly on ExecutionTraceRecord by ToolDiagnosticsMiddleware,
        // not through this processor. Unset/Unspecified status defaults to "success".
        var existingCategory = data.GetTagItem(ToolConventions.ResultCategory) as string;
        if (existingCategory is null)
        {
            var category = data.Status switch
            {
                ActivityStatusCode.Ok => TraceResultCategories.Success,
                ActivityStatusCode.Error => TraceResultCategories.Error,
                _ => TraceResultCategories.Success // Unset/Unspecified = success (tool completed without explicit error)
            };
            data.SetTag(ToolConventions.ResultCategory, category);
        }

        // Candidate ID from baggage — only present in optimization eval contexts
        var candidateId = data.GetBaggageItem(ToolConventions.HarnessCandidateId);
        if (candidateId is not null)
            data.SetTag(ToolConventions.HarnessCandidateId, candidateId);

        var iteration = data.GetBaggageItem(ToolConventions.HarnessIteration);
        if (iteration is not null)
            data.SetTag(ToolConventions.HarnessIteration, iteration);

        _logger.LogTrace(
            "CausalSpanAttributionProcessor enriched span {SpanId} for tool {ToolName}",
            data.SpanId, toolName);
    }
}
