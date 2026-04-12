using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
///   <item><description><c>tool.input_hash</c> — SHA-256 of the tool result tag (only when <c>IsAllDataRequested</c>)</description></item>
///   <item><description><c>tool.result_category</c> — bucketed outcome from span status</description></item>
///   <item><description><c>gen_ai.harness.candidate_id</c> — from Activity baggage when in an eval context</description></item>
///   <item><description><c>gen_ai.harness.iteration</c> — from Activity baggage when in an eval context</description></item>
/// </list>
/// </remarks>
public sealed class CausalSpanAttributionProcessor : BaseProcessor<Activity>
{
    private readonly ILogger<CausalSpanAttributionProcessor> _logger;

    /// <summary>Initializes a new instance of <see cref="CausalSpanAttributionProcessor"/>.</summary>
    public CausalSpanAttributionProcessor(ILogger<CausalSpanAttributionProcessor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        // Only process execute_tool spans
        var operationName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
        if (operationName != ToolConventions.ExecuteToolOperation)
            return;

        // Bridge agent.tool.name → gen_ai.tool.name (OTel GenAI semantic convention)
        var toolName = data.GetTagItem(ToolConventions.Name) as string;
        if (toolName is not null)
            data.SetTag(ToolConventions.GenAiToolName, toolName);

        // Input hash — SHA256 of tool arguments. Only when full data is requested (performance guard).
        if (data.IsAllDataRequested)
        {
            var inputValue = data.GetTagItem(ToolConventions.ToolCallArguments) as string ?? string.Empty;
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(inputValue));
            var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            data.SetTag(ToolConventions.InputHash, hashHex);
        }

        // Result category from span status or existing tag
        var existingCategory = data.GetTagItem(ToolConventions.ResultCategory) as string;
        if (existingCategory is null)
        {
            var category = data.Status switch
            {
                ActivityStatusCode.Ok => TraceResultCategories.Success,
                ActivityStatusCode.Error => TraceResultCategories.Error,
                _ => TraceResultCategories.Success // default to success for unset status
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
