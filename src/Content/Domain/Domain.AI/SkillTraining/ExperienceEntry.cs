namespace Domain.AI.SkillTraining;

/// <summary>
/// One lesson from a training epoch, stored so a later step or a later run can use it (#845).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the free-text "meta-skill memory" the loop used to carry: an entry is structured, belongs to
/// a domain, a skill, a run and an epoch, can be scanned for grader output before it is stored, and can be
/// forgotten by its position.
/// </para>
/// <para>
/// It carries no score. <see cref="EditVerdicts"/> says what the gate showed about each edit, derived by
/// the loop (see <see cref="EditVerdict"/>); nothing numeric from the grader is in an entry.
/// </para>
/// </remarks>
public sealed record ExperienceEntry
{
    /// <summary>When the lesson applies: the situation to recognise it by.</summary>
    public required string Cue { get; init; }

    /// <summary>What to do, or not do, in that situation.</summary>
    public required string Lesson { get; init; }

    /// <summary>What supports the lesson (what was tried and what the runs showed).</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>How far to trust the lesson, from 0 (a guess) to 1 (every edit it rests on was attributable).</summary>
    public required double Confidence { get; init; }

    /// <summary>The edits the lesson rests on and what the gate showed about each.</summary>
    public IReadOnlyList<EditTrial> EditVerdicts { get; init; } = [];

    /// <summary>Hash of <see cref="SkillContent"/> (SHA-256 of the UTF-8 text, lower-case hex).</summary>
    public required string SkillHash { get; init; }

    /// <summary>The skill document as it stood when the lesson was drawn.</summary>
    public required string SkillContent { get; init; }

    /// <summary>The domain the skill works in, used to group lessons across skills and runs.</summary>
    public required string Domain { get; init; }

    /// <summary>The skill the lesson is about.</summary>
    public required string SkillId { get; init; }

    /// <summary>The training run that drew the lesson.</summary>
    public required string RunId { get; init; }

    /// <summary>The epoch (1-based) that just finished when the lesson was drawn.</summary>
    public required int Epoch { get; init; }
}
