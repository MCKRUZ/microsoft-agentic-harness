namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The absolute directory the skill-training proposer's evidence trees are written under, fixed at
/// startup (#843).
/// </summary>
/// <remarks>
/// A single value shared by whoever writes the evidence (the agentic proposer) and whoever may read it
/// (the confined <c>evidence_reader</c> tool). Resolving it once, rather than each from live
/// configuration, keeps the two from ever disagreeing: a root changed by a configuration reload would
/// otherwise leave the proposer writing to one directory and the reader confined to another.
/// </remarks>
/// <param name="Path">The absolute, normalised evidence root.</param>
public sealed record SkillTrainingEvidenceRoot(string Path);
