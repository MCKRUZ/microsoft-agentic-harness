using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// Refuses to let the grader's own output reach the patch proposer: scans the text a proposer is
/// about to read for the shapes our evaluation results serialize to (#841).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tripwire, not the defence.</b> The defence is that <see cref="Domain.AI.SkillTraining.ProposerRolloutView"/>
/// carries no score. This catches the case where a verdict arrives some other way: a runner that
/// serializes its <c>EvalRunReport</c> into a trajectory, or a skill document that an earlier step
/// copied a result into. A hit means a read path is wrong; fix the path rather than widening or
/// narrowing the marker list to make a run go through.
/// </para>
/// <para>
/// <b>Markers are quoted JSON keys</b> of our own result types (<c>EvalRunReport</c>, <c>RolloutResult</c>),
/// matched case-insensitively, because a bare word like "score" appears in ordinary tool output and
/// would make the tripwire cry wolf. The list is deliberately short and tied to types in this repo.
/// </para>
/// </remarks>
public static class GraderMarkerScanner
{
    /// <summary>The stable failure code a handler returns when a scan finds a leak.</summary>
    public const string EvidenceLeakCode = "skill_training.evidence_leak";

    /// <summary>The marker substrings, lower-case, matched case-insensitively.</summary>
    public static IReadOnlyList<string> Markers { get; } =
    [
        "\"overallverdict\"",
        "\"passedcount\"",
        "\"failedcount\"",
        "\"warnedcount\"",
        "\"erroredcount\"",
        "\"hard\":",
        "\"soft\":",
        "\"issuccess\"",
    ];

    /// <summary>
    /// Scans everything a patch proposer is about to read: the skill, the cross-epoch memory and each
    /// rollout's trajectory. Empty when the evidence is clean.
    /// </summary>
    /// <param name="input">The proposer's input.</param>
    public static IReadOnlyList<GraderLeakFinding> Scan(ReflectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Scan(EvidenceItems(input));
    }

    /// <summary>Source and marker for each finding (at most eight), never the scanned text.</summary>
    /// <param name="findings">The findings to describe.</param>
    public static string Describe(IReadOnlyList<GraderLeakFinding> findings) =>
        string.Join("; ", findings.Take(8).Select(f => $"{f.Source} has {f.Marker} at {f.Offset}"));

    /// <summary>
    /// Scans each item and returns every marker found, in item order. Empty when the evidence is clean.
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

            foreach (var marker in Markers)
            {
                var offset = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (offset >= 0)
                    findings.Add(new GraderLeakFinding(source, marker, offset));
            }
        }

        return findings;
    }

    private static IEnumerable<(string Source, string? Text)> EvidenceItems(ReflectionInput input)
    {
        yield return ("skill", input.CurrentSkill);
        yield return ("meta-skill-memory", input.MetaSkillMemory);
        foreach (var rollout in input.Rollouts)
            yield return ($"rollout:{rollout.ItemId}", rollout.Trajectory);
    }
}
