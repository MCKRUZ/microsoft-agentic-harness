using Domain.AI.Skills;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// #618: parses a candidate's materialized skill directory into a <see cref="SkillDefinition"/> the
/// eval run can hand to <c>GoverningToolContextProvider</c>, so <c>run_skill_script</c> resolves the
/// candidate's own <see cref="EgressManifest"/> allowlist during evaluation instead of silently
/// falling back to the harness-wide default. Split from the main file per this project's Partial
/// Class Pattern.
/// </summary>
public sealed partial class AgentEvaluationService
{
    /// <summary>
    /// Builds a <see cref="SkillDefinition"/> for the candidate's bare top-level skill, parsed
    /// straight from the SAME materialized directory <c>MaterializeCandidateSkills</c> just wrote —
    /// never re-derived independently, so this can never disagree with what was actually written to
    /// disk.
    /// </summary>
    /// <param name="skillDirectory">
    /// The directory <c>MaterializeCandidateSkills</c> returned, or <see langword="null"/> when the
    /// candidate proposed no skill files at all.
    /// </param>
    /// <param name="bareSkillName">
    /// The subdirectory name <c>MaterializeCandidateSkills</c> nested the bare skill's files under
    /// (its own declared frontmatter name), or <see langword="null"/> for a multi-skill/sibling-only
    /// materialization with no bare top-level <c>SKILL.md</c>. The bare skill's manifest lives at
    /// <c>{skillDirectory}/{bareSkillName}/SKILL.md</c>, never at <c>{skillDirectory}/SKILL.md</c>
    /// directly — re-deriving this name independently here risks disagreeing with what
    /// <c>MaterializeCandidateSkills</c> actually wrote (the exact class of bug #618's sibling-naming
    /// fix, PR #668, was written to prevent).
    /// </param>
    /// <param name="reader">
    /// The single reader confined to <paramref name="skillDirectory"/>, shared with
    /// <c>BuildContextProviders</c>'s progressive-disclosure provider rather than each building its
    /// own (/simplify efficiency finding on #618's PR: two independent readers over the identical
    /// root did the same sandbox-guard setup twice for no reason). Never null when
    /// <paramref name="skillDirectory"/> is non-null — see <c>RunCandidateTurnAsync</c>, the only
    /// caller.
    /// </param>
    /// <param name="executionRunId">Used only for diagnostic logging.</param>
    /// <returns>
    /// The parsed candidate skill, or <see langword="null"/> when there is no skill directory or no
    /// bare skill name — #618 scoped this fix to the common single-skill case (see
    /// <see cref="Application.AI.Common.Services.Governance.EphemeralSkillMetadataAccessor"/>'s
    /// remarks).
    /// </returns>
    /// <exception cref="Exception">
    /// Propagates whatever <see cref="SkillMetadataParser.ParseFromFile"/> throws — including a
    /// prompt-injection refusal or an egress-manifest validation failure — deliberately uncaught.
    /// A candidate's proposed skill body is LLM-authored, untrusted content; a refusal here is a
    /// genuine finding about the candidate under test, not an eval-harness defect to swallow. The
    /// caller's existing task-level catch turns this into a failed (not crashed) eval task.
    /// </exception>
    private SkillDefinition? TryBuildCandidateSkillDefinition(
        string? skillDirectory, string? bareSkillName, MaterializedSkillDirectoryFileReader? reader, Guid executionRunId)
    {
        if (skillDirectory is null || bareSkillName is null)
        {
            if (skillDirectory is not null)
            {
                // Multi-skill/sibling-bundling shape: MaterializeCandidateSkills already wrote a
                // valid set of files (it would have thrown otherwise), just not with a bare
                // top-level SKILL.md. Debug, not warning — a known, deliberately deferred shape,
                // not a defect (#618).
                _logger.LogDebug(
                    "Execution run {ExecutionRunId}: materialized skill directory has no bare " +
                    "top-level SKILL.md — #618 egress scoping is not applied for this " +
                    "multi-skill/sibling shape.",
                    executionRunId);
            }

            return null;
        }

        var bareSkillDirectory = Path.Combine(skillDirectory, bareSkillName);
        var skillFilePath = Path.Combine(bareSkillDirectory, "SKILL.md");

        var parser = new SkillMetadataParser(
            _loggerFactory.CreateLogger<SkillMetadataParser>(), reader!, _scanner, _aiConfig, _egressValidator);

        return parser.ParseFromFile(skillFilePath, bareSkillDirectory);
    }
}
