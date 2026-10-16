using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Application.AI.Common.Services.Tools;
using Application.Common.Interfaces.Common;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.SkillTraining;
using Domain.Common.Config.AI.SkillTraining;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// The skill-training loop's patch proposer, run as a governed agent session over evidence on disk (#843).
/// </summary>
/// <remarks>
/// <para>
/// <b>How a step works.</b> The step's evidence (the current skill, every rollout with its full
/// trajectory, any cross-epoch memory) is written to a folder under the evidence root. A
/// <c>skill-optimizer</c> agent session is dispatched through <see cref="IMediator"/>, so it runs
/// through the same pipeline as any other agent: content safety, prompt-injection screening, token
/// budget, audit and the tool-call admission chokepoint. It reads the folder with <c>evidence_reader</c>,
/// the only tool it holds, and replies with the complete new skill document or with KEEP_BASE.
/// </para>
/// <para>
/// <b>Why the loop's fence still holds.</b> The session cannot write: it holds a tool with no write
/// operation. What it returns is text, which is turned into the loop's bounded
/// <see cref="Edit"/>s by <see cref="SkillDocumentDiff"/>; those edits must reproduce the returned
/// document byte for byte through the real <see cref="PatchApplier"/> and pass
/// <see cref="HarnessPatchValidator"/> before they are returned. The loop then applies its own intake
/// checks, the editable-surface fence and the accept gate exactly as for any other proposer. A proposal
/// that fails verification is sent back once with the reason; a second failure returns an empty patch
/// (the loop records a reject) rather than guessing.
/// </para>
/// <para>
/// <b>What it is never shown.</b> No score and no judge text: <see cref="ProposerRolloutView"/> carries a
/// pass/fail flag and the trajectory, the evidence is scanned for grader output once more where it is
/// written to disk (a leaking rollout is left out, a leaking skill or memory file ends the step), and the
/// configured work description may not name its measurement.
/// </para>
/// <para>
/// <b>What it can read.</b> Only this step's folder: <see cref="EvidenceReadScope"/> is published
/// around the session, and the evidence tool refuses any other path, including a sibling step's.
/// </para>
/// </remarks>
public sealed class AgenticPatchProposer : IPatchProposer
{
    private const int Attempts = 2;

    private readonly IMediator _mediator;
    private readonly IOwnerOnlyDirectoryCreator _directories;
    private readonly SkillTrainingEvidenceRoot _evidenceRoot;
    private readonly IOptionsMonitor<SkillTrainingConfig> _config;
    private readonly PatchApplier _applier;
    private readonly HarnessPatchValidator _fence;
    private readonly ILogger<AgenticPatchProposer> _logger;

    /// <summary>Initializes the proposer.</summary>
    public AgenticPatchProposer(
        IMediator mediator,
        IOwnerOnlyDirectoryCreator directories,
        SkillTrainingEvidenceRoot evidenceRoot,
        IOptionsMonitor<SkillTrainingConfig> config,
        PatchApplier applier,
        HarnessPatchValidator fence,
        ILogger<AgenticPatchProposer> logger)
    {
        ArgumentNullException.ThrowIfNull(mediator);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(evidenceRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(logger);
        _mediator = mediator;
        _directories = directories;
        _evidenceRoot = evidenceRoot;
        _config = config;
        _applier = applier;
        _fence = fence;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        _directories.Create(_evidenceRoot.Path);
        var tree = await ProposerEvidenceTree.WriteAsync(_evidenceRoot.Path, input, _directories, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // The loop screened this evidence before calling; this is the boundary where it reaches disk
            // and then a model, so it is checked again rather than trusted (a different caller, such as
            // the standalone reflect command, would not have the same history). A rollout that leaks is
            // left out and the rest go ahead, as in the loop: aborting on any marker would hand the
            // writer of one hostile trajectory a switch that stops the whole step.
            var screened = await ProposerEvidenceTree.ScreenAsync(tree, cancellationToken).ConfigureAwait(false);
            if (screened.Findings.Count > 0)
            {
                _logger.LogWarning(
                    "Skill-training proposer evidence carried grader-verdict marker(s) [{Findings}]; usable: {Usable}",
                    GraderMarkerScanner.Describe(screened.Findings),
                    screened.Usable);
            }

            if (!screened.Usable)
                return Empty("Evidence carried grader-verdict marker(s); no proposal was requested.");

            return await ProposeFromEvidenceAsync(input, screened.Tree, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ProposerEvidenceTree.Delete(tree.Directory);
        }
    }

    private async Task<Patch> ProposeFromEvidenceAsync(
        ReflectionInput input, ProposerEvidenceTree.Written tree, CancellationToken cancellationToken)
    {
        var mission = BuildMission(tree, _config.CurrentValue.WorkDescription);
        var rejection = string.Empty;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var response = await RunSessionAsync(mission + rejection, tree.Directory, cancellationToken).ConfigureAwait(false);
            var reply = ProposerReply.Parse(response);

            if (reply.Skill is null)
            {
                if (reply.SaysKeepBase)
                    return Empty(reply.Notes);

                rejection = Rejected($"the reply had no {ProposerReply.BeginSkill} block and did not say KEEP_BASE");
                continue;
            }

            if (reply.IsAmbiguous)
            {
                rejection = Rejected(
                    $"the document contains, or is followed by, a line that is itself {ProposerReply.BeginSkill} or {ProposerReply.EndSkill}, " +
                    "so where it ends cannot be told. Reply with one block only");
                continue;
            }

            var proposed = AlignToCurrent(input.CurrentSkill, reply.Skill);
            if (string.IsNullOrWhiteSpace(proposed))
            {
                rejection = Rejected("the proposed document is empty; to leave the skill alone reply KEEP_BASE");
                continue;
            }

            if (string.Equals(proposed, input.CurrentSkill, StringComparison.Ordinal))
                return Empty(reply.Notes);

            var patch = new Patch { Edits = SkillDocumentDiff.Between(input.CurrentSkill, proposed), Reasoning = reply.Notes };
            var problem = Verify(_applier, _fence, input.CurrentSkill, proposed, patch);
            if (problem is null)
                return patch;

            _logger.LogWarning("Skill-training proposal rejected on attempt {Attempt}: {Problem}", attempt, problem);
            rejection = Rejected(problem);
        }

        return Empty("The proposer did not produce a usable proposal after a retry; nothing was proposed.");
    }

    /// <summary>
    /// Confirms the edits do what the proposer's document says: through the real applier they reproduce it
    /// exactly, and the fence allows them. Returns the reason when they do not.
    /// </summary>
    internal static string? Verify(PatchApplier applier, HarnessPatchValidator fence, string current, string proposed, Patch patch)
    {
        var report = applier.Apply(current, patch);
        if (report.FailedEdits.Count > 0 || !string.Equals(report.NewSkillContent, proposed, StringComparison.Ordinal))
            return "the document could not be expressed as bounded edits that reproduce it exactly";

        var verdict = fence.Validate(patch);
        return verdict.IsAllowed ? null : "the proposal touches a surface the loop may not edit";
    }

    private async Task<string> RunSessionAsync(string mission, string evidenceDirectory, CancellationToken cancellationToken)
    {
        var config = _config.CurrentValue;

        // The evidence tool serves only this step's folder, for the length of this one awaited call.
        using var readable = EvidenceReadScope.Begin(evidenceDirectory);
        var result = await _mediator.Send(
            new RunConversationCommand
            {
                AgentName = config.ProposerAgentName,
                UserMessages = [mission],
                MaxTurns = config.MaxProposerTurns,
                ConversationId = $"skill-training-{Guid.NewGuid():N}"
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            // The full detail goes to the log; the exception carries none of it (a session error can
            // quote URLs and headers), so it is safe to surface to the loop's own logging.
            _logger.LogWarning("Skill-training proposer session failed: {Error}", result.Error);
            throw new InvalidOperationException("The skill-training proposer session did not complete.");
        }

        return result.FinalResponse;
    }

    /// <summary>
    /// The document the proposer returned, written the way the current skill is: its trailing whitespace
    /// copied verbatim (the reply format cannot say whether a final newline is intended) and, when the
    /// current skill uses Windows line endings throughout, its line endings. Without this a skill saved
    /// with CRLF would come back as a rewrite of every line, and one difference at the end alone would
    /// show up as a change.
    /// </summary>
    private static string AlignToCurrent(string current, string proposed)
    {
        var trailing = current[current.TrimEnd().Length..];
        var body = proposed.TrimEnd();
        if (UsesCrLfThroughout(current))
            body = body.Replace("\n", "\r\n", StringComparison.Ordinal);

        return body + trailing;
    }

    private static bool UsesCrLfThroughout(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal)
        && !text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n');

    private static string BuildMission(ProposerEvidenceTree.Written tree, string workDescription)
    {
        var dir = tree.Directory.Replace('\\', '/');
        var work = string.IsNullOrWhiteSpace(workDescription)
            ? string.Empty
            : $"# THE WORK\n\n{workDescription.Trim()}\n\n";
        var memory = tree.HasMemory ? $"  {dir}/memory/EXPERIENCE.md    lessons from earlier rounds\n" : string.Empty;

        return
            $"You are improving one skill document.\n\n{work}" +
            "# YOUR EVIDENCE\n\n" +
            "It is on disk. Read it with the evidence_reader tool, using the absolute paths below; do not answer from this message alone.\n\n" +
            $"  {dir}/skill/SKILL.md              the current skill document\n" +
            $"  {dir}/rollouts/                   {tree.RolloutCount} run(s): {tree.PassedCount} passed, {tree.RolloutCount - tree.PassedCount} did not\n" +
            memory +
            "\nOpen every run before you decide. Reply in the format your skill describes.";
    }

    private static string Rejected(string reason) =>
        $"\n\n# YOUR PREVIOUS ATTEMPT WAS REJECTED\n\n{reason}.\n\nReply again in the same format.";

    private static Patch Empty(string reasoning) => new() { Edits = [], Reasoning = reasoning };
}
