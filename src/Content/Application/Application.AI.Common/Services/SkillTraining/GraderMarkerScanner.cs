using System.Text.RegularExpressions;
using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// Keeps the grader's own output away from the patch proposer: finds the shapes our evaluation results
/// serialize to in the text a proposer is about to read (#841).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tripwire, not the defence.</b> The defence is that <see cref="ProposerRolloutView"/> carries no
/// score. This catches the case where a verdict arrives some other way: a runner that serializes its
/// <c>EvalRunReport</c> into a trajectory, or an edit that copies a result into the skill. A hit means a
/// read path is wrong; fix the path rather than editing the lists to make a run go through.
/// </para>
/// <para>
/// <b>Specific on purpose.</b> Trajectories are tool output, so some of it is attacker-influenced, and a
/// refusal triggered by a common string is a lever to abort training runs. The markers are therefore JSON
/// keys unique to <c>EvalRunReport</c> (<see cref="VerdictKeys"/>), plus the <c>Hard</c> and <c>Soft</c>
/// pair of <c>RolloutResult</c> only when BOTH appear (<see cref="ScoreKeys"/>): either alone is an
/// ordinary key. Matching tolerates a JSON-escaped quote and whitespace before the colon, because the
/// likeliest real leak is a report serialized into a JSON trajectory. <c>GraderMarkerScannerTests</c>
/// ties both lists to the real properties, so a rename fails a test instead of silently emptying the
/// tripwire. What it cannot see: a verdict written as prose, YAML, CSV or a table.
/// </para>
/// </remarks>
public static class GraderMarkerScanner
{
    /// <summary>The stable failure code a handler returns when a scan leaves nothing safe to show.</summary>
    public const string EvidenceLeakCode = "skill_training.evidence_leak";

    /// <summary>JSON keys specific to <c>EvalRunReport</c>; any one of them is a leak.</summary>
    public static IReadOnlyList<string> VerdictKeys { get; } =
        ["OverallVerdict", "PassedCount", "FailedCount", "WarnedCount", "ErroredCount"];

    /// <summary>
    /// JSON keys of <c>RolloutResult</c>'s two scores; a leak only when both appear in the same text.
    /// </summary>
    public static IReadOnlyList<string> ScoreKeys { get; } = ["Hard", "Soft"];

    private static readonly Regex[] VerdictPatterns = [.. VerdictKeys.Select(KeyPattern)];
    private static readonly Regex[] ScorePatterns = [.. ScoreKeys.Select(KeyPattern)];

    /// <summary>
    /// Scans each labelled text and returns every marker found, in item order. Empty when clean.
    /// </summary>
    /// <param name="items">Labelled texts the proposer is about to read.</param>
    public static IReadOnlyList<GraderLeakFinding> Scan(IEnumerable<(string Source, string? Text)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var findings = new List<GraderLeakFinding>();
        foreach (var (source, text) in items)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            for (var i = 0; i < VerdictPatterns.Length; i++)
            {
                var match = VerdictPatterns[i].Match(text);
                if (match.Success)
                    findings.Add(new GraderLeakFinding(source, VerdictKeys[i], match.Index));
            }

            var scoreMatches = ScorePatterns.Select(p => p.Match(text)).ToArray();
            if (scoreMatches.All(m => m.Success))
                findings.Add(new GraderLeakFinding(source, string.Join("+", ScoreKeys), scoreMatches.Min(m => m.Index)));
        }

        return findings;
    }

    /// <summary>
    /// Splits rollouts into the ones safe to show a proposer and the findings for the rest. A rollout
    /// whose trajectory carries a marker is left out of what the proposer reads (the gate still scores
    /// it), so a hostile tool output can remove its own rollout from the proposer's view but cannot
    /// abort the run.
    /// </summary>
    /// <param name="rollouts">The proposer-facing rollouts.</param>
    public static ScreenedRollouts Screen(IReadOnlyList<ProposerRolloutView> rollouts)
    {
        ArgumentNullException.ThrowIfNull(rollouts);

        var clean = new List<ProposerRolloutView>(rollouts.Count);
        var findings = new List<GraderLeakFinding>();
        foreach (var rollout in rollouts)
        {
            var found = Scan([($"rollout:{rollout.ItemId}", rollout.Trajectory)]);
            if (found.Count == 0)
                clean.Add(rollout);
            else
                findings.AddRange(found);
        }

        return new ScreenedRollouts(clean, findings);
    }

    /// <summary>Source and marker for each finding (at most eight), never the scanned text.</summary>
    /// <param name="findings">The findings to describe.</param>
    public static string Describe(IReadOnlyList<GraderLeakFinding> findings) =>
        string.Join("; ", findings.Take(8).Select(f => $"{f.Source} has {f.Marker} at {f.Offset}"));

    private static Regex KeyPattern(string key) =>
        new($"\\\\*\"{Regex.Escape(key)}\\\\*\"\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
