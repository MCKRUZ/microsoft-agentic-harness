using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Span processor that computes a composite usefulness score (0-1) for each
/// tool execution span. Scores are based on four weighted signals:
/// result quality (0.40), sequential chain (0.30), substance (0.20), and result size (0.10).
/// </summary>
/// <remarks>
/// Chain detection uses parent span child count as a heuristic — a tool called
/// as part of a multi-tool sequence scores higher than an isolated call.
/// True cross-turn reference detection would require message-level analysis
/// which is outside the scope of span processing.
/// </remarks>
public sealed class ToolUsefulnessProcessor : BaseProcessor<Activity>
{
    private const double WeightResultQuality = 0.40;
    private const double WeightChainDetection = 0.30;
    private const double WeightSubstance = 0.20;
    private const double WeightResultSize = 0.10;
    private const int SubstantialResultLength = 100;
    private const int LargeResultLength = 1000;

    private readonly ILogger<ToolUsefulnessProcessor> _logger;

    public ToolUsefulnessProcessor(ILogger<ToolUsefulnessProcessor> logger)
    {
        _logger = logger;
        _logger.LogInformation("Tool usefulness processor initialized");
    }

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        var opName = data.GetTagItem(ToolConventions.GenAiOperationName) as string;
        if (!string.Equals(opName, ToolConventions.ExecuteToolOperation, StringComparison.Ordinal))
            return;

        var toolName = data.GetTagItem(ToolConventions.Name) as string ?? "unknown";
        var agentName = data.GetTagItem(AgentConventions.Name) as string ?? "unknown";
        var result = data.GetTagItem(ToolConventions.ToolCallResult) as string;

        var score = ComputeScore(data, result);

        ToolUsefulnessMetrics.UsefulnessScore.Record(score, new TagList
        {
            { ToolConventions.Name, toolName },
            { AgentConventions.Name, agentName }
        });
    }

    private static double ComputeScore(Activity data, string? result)
    {
        var qualityScore = ScoreResultQuality(data, result);
        var chainScore = ScoreChainDetection(data);
        var substanceScore = ScoreSubstance(result);
        var sizeScore = ScoreResultSize(result);

        return (qualityScore * WeightResultQuality)
             + (chainScore * WeightChainDetection)
             + (substanceScore * WeightSubstance)
             + (sizeScore * WeightResultSize);
    }

    private static double ScoreResultQuality(Activity data, string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0.0;
        if (data.Status == ActivityStatusCode.Error) return 0.1;
        if (result.Length < 10) return 0.3;
        return result.Length >= SubstantialResultLength ? 1.0 : 0.6;
    }

    private static double ScoreChainDetection(Activity data)
    {
        // Heuristic: if the parent span has multiple tool-execution children,
        // this tool is part of a chain (LLM called multiple tools in sequence)
        var parent = data.Parent;
        if (parent is null) return 0.5;

        var siblingCount = 0;
        foreach (var link in parent.Links)
        {
            siblingCount++;
            if (siblingCount > 1) return 1.0;
        }

        // Fallback: check if parent has child spans via enumerating
        // (Activity doesn't expose children directly; use duration heuristic)
        return parent.Duration > data.Duration * 2 ? 0.8 : 0.5;
    }

    private static double ScoreSubstance(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0.0;

        var score = 0.0;
        var span = result.AsSpan();

        // JSON structure markers
        if (span.Contains("{".AsSpan(), StringComparison.Ordinal) && span.Contains("}".AsSpan(), StringComparison.Ordinal))
            score += 0.3;

        // Code markers (common patterns)
        if (span.Contains("```".AsSpan(), StringComparison.Ordinal) || span.Contains("=>".AsSpan(), StringComparison.Ordinal))
            score += 0.3;

        // List/structured output
        if (span.Contains("\n- ".AsSpan(), StringComparison.Ordinal) || span.Contains("\n* ".AsSpan(), StringComparison.Ordinal))
            score += 0.2;

        // Has multiple lines (structured output)
        if (result.Count(c => c == '\n') >= 3)
            score += 0.2;

        return Math.Min(score, 1.0);
    }

    private static double ScoreResultSize(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0.0;
        if (result.Length >= LargeResultLength) return 1.0;
        if (result.Length >= SubstantialResultLength) return 0.7;
        return result.Length >= 20 ? 0.4 : 0.1;
    }
}
