using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>
/// The write-side scorecard for a single harmonic memory mode (Off / AbstractOnly / Full). Nullable metrics
/// are <see langword="null"/> when they do not apply to the mode (Off produces no abstractions) or were not
/// measured (quality judging is skipped offline).
/// </summary>
public sealed record HarmonicWriteModeResult
{
    /// <summary>The harmonic memory mode this row reports.</summary>
    public required string Mode { get; init; }

    /// <summary>Number of facts remembered.</summary>
    public required int FactCount { get; init; }

    /// <summary>Ground-truth number of distinct topics the facts belong to (the ideal distinct-abstraction count).</summary>
    public required int GoldTopicCount { get; init; }

    /// <summary>Distinct abstractions produced across all facts. <see langword="null"/> for Off (no abstractions).</summary>
    public int? DistinctAbstractions { get; init; }

    /// <summary>
    /// <see cref="DistinctAbstractions"/> ÷ <see cref="GoldTopicCount"/>. 1.0 = perfectly consolidated to one
    /// abstraction per real topic; higher = more fragmented. <see langword="null"/> for Off.
    /// </summary>
    public double? FragmentationRatio { get; init; }

    /// <summary>
    /// Weighted fraction of facts whose abstraction-group is dominated by a single gold topic, in [0, 1].
    /// 1.0 = every group is topically pure (no unrelated facts merged together). <see langword="null"/> for Off.
    /// </summary>
    public double? ClusterPurity { get; init; }

    /// <summary>Abstractor invocations (the AbstractOnly/Full per-fact LLM cost). 0 for Off.</summary>
    public required int AbstractorCalls { get; init; }

    /// <summary>Consolidator invocations (the Full-mode incremental LLM cost). 0 for Off and AbstractOnly.</summary>
    public required int ConsolidatorCalls { get; init; }

    /// <summary>Total write-time LLM calls (<see cref="AbstractorCalls"/> + <see cref="ConsolidatorCalls"/>).</summary>
    [JsonIgnore]
    public int TotalLlmCalls => AbstractorCalls + ConsolidatorCalls;

    /// <summary>Mean abstraction-quality score in [0, 1] from the LLM judge. <see langword="null"/> when not judged.</summary>
    public double? MeanAbstractionQuality { get; init; }

    /// <summary>Number of abstractions that were successfully quality-judged (contributing to the mean).</summary>
    public int AbstractionsJudged { get; init; }
}

/// <summary>The full harmonic write-eval report across all modes.</summary>
public sealed record HarmonicWriteEvalReport
{
    /// <summary>Description of the fixture that was run.</summary>
    public string? FixtureDescription { get; init; }

    /// <summary>Number of facts in the fixture.</summary>
    public required int FactCount { get; init; }

    /// <summary>Number of distinct gold topics in the fixture.</summary>
    public required int GoldTopicCount { get; init; }

    /// <summary>Whether the run used the paid LLM providers (true) or the offline deterministic providers (false).</summary>
    public required bool UsedLlm { get; init; }

    /// <summary>When the run completed (UTC, ISO-8601). Stamped by the caller.</summary>
    public required string GeneratedAtUtc { get; init; }

    /// <summary>Per-mode scorecards, in Off / AbstractOnly / Full order.</summary>
    public required IReadOnlyList<HarmonicWriteModeResult> Modes { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes the report to indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Renders a compact human-readable scorecard.</summary>
    public string ToConsole()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Harmonic memory write-side eval");
        if (!string.IsNullOrWhiteSpace(FixtureDescription))
            sb.AppendLine($"  fixture: {FixtureDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  facts: {FactCount}   gold topics: {GoldTopicCount}   providers: {(UsedLlm ? "LLM (paid)" : "deterministic (offline)")}");
        sb.AppendLine();
        sb.AppendLine($"  {"mode",-13}{"abstractions",13}{"fragment",10}{"purity",9}{"llm-calls",11}{"quality",9}");
        sb.AppendLine($"  {new string('-', 62)}");
        foreach (var m in Modes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {m.Mode,-13}{Cell(m.DistinctAbstractions),13}{Ratio(m.FragmentationRatio),10}{Ratio(m.ClusterPurity),9}{m.TotalLlmCalls,11}{Ratio(m.MeanAbstractionQuality),9}");
        }
        sb.AppendLine();
        sb.AppendLine("  fragment  = distinct abstractions / gold topics (1.0 = fully consolidated; higher = fragmented)");
        sb.AppendLine("  purity    = fraction of facts in topically-pure abstraction groups (1.0 = no unrelated merges)");
        sb.AppendLine("  llm-calls = WRITE-TIME calls only (abstractor + consolidator); --llm adds ~2x quality-judge calls");
        sb.AppendLine("  quality   = mean LLM-judged abstraction quality (blank offline)");
        return sb.ToString();
    }

    private static string Cell(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Ratio(double? value) =>
        value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "-";
}

/// <summary>Pure metric computations over a fact → stored-abstraction mapping. Stateless and unit-testable.</summary>
public static class HarmonicWriteMetrics
{
    /// <summary>Distinct abstractions (case-insensitive) across the assignments.</summary>
    public static int DistinctAbstractions(IReadOnlyList<FactAbstraction> assignments) =>
        assignments.Select(a => a.Abstraction).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Fragmentation ratio = distinct abstractions ÷ gold-topic count. Returns 0 when there are no gold topics.</summary>
    public static double FragmentationRatio(int distinctAbstractions, int goldTopicCount) =>
        goldTopicCount == 0 ? 0 : (double)distinctAbstractions / goldTopicCount;

    /// <summary>
    /// Cluster purity: group facts by abstraction, take each group's dominant gold-topic share, and weight by
    /// group size. 1.0 means every abstraction group contains facts from a single gold topic.
    /// </summary>
    public static double ClusterPurity(IReadOnlyList<FactAbstraction> assignments)
    {
        if (assignments.Count == 0) return 0;

        var dominant = assignments
            .GroupBy(a => a.Abstraction, StringComparer.OrdinalIgnoreCase)
            .Sum(group => group
                .GroupBy(a => a.GoldTopic, StringComparer.OrdinalIgnoreCase)
                .Max(t => t.Count()));

        return (double)dominant / assignments.Count;
    }
}

/// <summary>One fact's stored abstraction paired with its ground-truth gold topic, for metric computation.</summary>
public sealed record FactAbstraction
{
    /// <summary>The stored abstraction the write path produced for the fact.</summary>
    public required string Abstraction { get; init; }

    /// <summary>The fact's ground-truth gold topic.</summary>
    public required string GoldTopic { get; init; }
}
