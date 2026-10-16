using System.Text;
using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The default <see cref="IExperienceDistiller"/> (#845): writes the epoch's lesson from the gate's
/// verdicts on the edits that were tried, with no model call.
/// </summary>
/// <remarks>
/// <para>
/// The lesson is what the loop knows for certain: which edits were kept, which made things worse, which
/// changed nothing, and which went in together with others so that nothing can be said about them. That is
/// the part of a model-written lesson a proposer can rely on, and the part most worth having: it stops the
/// proposer proposing the same rejected edit again.
/// </para>
/// <para>
/// Confidence is the share of tried edits whose verdict could be attributed to them, so a step that bundled
/// several edits lowers it.
/// </para>
/// </remarks>
public sealed class EditHistoryDistiller : IExperienceDistiller
{
    /// <summary>The most trials listed in a lesson; the rest are counted.</summary>
    public const int MaxListedTrials = 20;

    /// <inheritdoc />
    public Task<DistillationOutcome> DistillAsync(DistillationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var hasGuidance = !string.IsNullOrWhiteSpace(input.PairedGuidance);
        if (input.Trials.Count == 0 && !hasGuidance)
            return Task.FromResult(DistillationOutcome.None("No edit reached the gate this epoch, so there is nothing to learn from."));

        var entry = new ExperienceEntry
        {
            Cue = $"Changing the '{input.SkillId}' skill ({input.Domain})",
            Lesson = input.Trials.Count > 0 ? Lesson(input.Trials) : input.PairedGuidance.Trim(),
            Evidence = Evidence(input, hasGuidance),
            Confidence = input.Trials.Count == 0
                ? 0.5
                : (double)input.Trials.Count(t => t.Verdict != EditVerdict.Confounded) / input.Trials.Count,
            EditVerdicts = input.Trials,
            SkillHash = SkillContentHash.Of(input.CurrentSkill),
            SkillContent = input.CurrentSkill,
            Domain = input.Domain,
            SkillId = input.SkillId,
            RunId = input.RunId,
            Epoch = input.Epoch
        };

        return Task.FromResult(DistillationOutcome.Written(entry));
    }

    private static string Lesson(IReadOnlyList<EditTrial> trials)
    {
        var text = new StringBuilder();
        foreach (var trial in trials.Take(MaxListedTrials))
            text.Append("- ").Append(Phrase(trial.Verdict)).Append(trial.Description).Append('\n');

        if (trials.Count > MaxListedTrials)
            text.Append("- (and ").Append(trials.Count - MaxListedTrials).Append(" more)\n");

        return text.ToString().TrimEnd();
    }

    private static string Phrase(EditVerdict verdict) => verdict switch
    {
        EditVerdict.Helped => "Kept: ",
        EditVerdict.Hurt => "Made it worse, do not repeat: ",
        EditVerdict.NoObservedEffect => "Changed nothing, not worth repeating: ",
        _ => "Tried together with other edits, unclear which mattered: "
    };

    private static string Evidence(DistillationInput input, bool hasGuidance)
    {
        var count = (EditVerdict v) => input.Trials.Count(t => t.Verdict == v);
        var summary = input.Trials.Count == 0
            ? $"Epoch {input.Epoch}: no edits were gated."
            : $"Epoch {input.Epoch}: {input.Trials.Count} edit(s) were tried: {count(EditVerdict.Helped)} kept, " +
              $"{count(EditVerdict.Hurt)} made it worse, {count(EditVerdict.NoObservedEffect)} changed nothing, " +
              $"{count(EditVerdict.Confounded)} unclear.";

        return hasGuidance && input.Trials.Count > 0
            ? summary + " Paired re-run: " + input.PairedGuidance.Trim()
            : summary;
    }
}
