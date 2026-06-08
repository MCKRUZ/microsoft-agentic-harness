using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Domain.AI.Changes;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Orchestration.Magentic;

/// <summary>
/// Inspects a Magentic replan event and, when the replan proposes a
/// state-changing action, routes the action through the PR-2
/// <see cref="SubmitChangeProposalCommand"/> instead of letting it execute
/// directly inside the Magentic workflow.
/// </summary>
/// <remarks>
/// <para>
/// MAF's replan event carries the new <c>FullTaskLedger</c> only as a chat
/// message; the per-action shape lives inside the manager's text. The router
/// matches against a small set of state-change verbs in the replan reason text
/// (default: <c>apply</c>, <c>deploy</c>, <c>write</c>, <c>patch</c>,
/// <c>merge</c>, <c>push</c>, <c>mutate</c>, <c>modify</c>). When matched the
/// replan is submitted as a <see cref="ChangeProposal"/> in <c>Draft</c> status
/// targeted at a placeholder <see cref="ChangeTargetKind"/>. PR-8 / PR-9 / PR-10
/// replace the placeholder with the per-target-kind skill packs that carry the
/// concrete target + diff.
/// </para>
/// <para>
/// This router is fail-soft: a non-matching replan or a
/// <see cref="Result{T}.Fail"/> from the submit command is logged and dropped
/// — the workflow proceeds. Failure to submit a proposal does NOT block the
/// workflow because the change-proposal pipeline is opt-in via
/// <c>AppConfig.AI.Changes.Enabled</c>; consumers who haven't enabled it would
/// see every replan log a noisy error otherwise.
/// </para>
/// </remarks>
public sealed class MagenticChangeProposalRouter
{
    private readonly IMediator _mediator;
    private readonly ILogger<MagenticChangeProposalRouter> _logger;

    private static readonly string[] DefaultStateChangeVerbs =
    {
        "apply",
        "deploy",
        "write",
        "patch",
        "merge",
        "push",
        "mutate",
        "modify"
    };

    /// <summary>
    /// Creates a router that submits qualifying replans via MediatR.
    /// </summary>
    public MagenticChangeProposalRouter(IMediator mediator, ILogger<MagenticChangeProposalRouter> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Classify a replan and, when it proposes a state-change action, submit a
    /// <see cref="ChangeProposal"/>. Returns the submitted proposal or
    /// <see langword="null"/> when nothing was routed (non-state-change or
    /// submission failed).
    /// </summary>
    /// <param name="info">Replan descriptor built by the event subscriber.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<ChangeProposal?> TryRouteAsync(MagenticReplanInfo info, CancellationToken ct)
    {
        if (!IsStateChange(info.ReplanText))
        {
            _logger.LogDebug(
                "Magentic replan not classified as state change: workflow={WorkflowId} version={PlanVersion}",
                info.WorkflowId,
                info.PlanVersion);
            return null;
        }

        var command = new SubmitChangeProposalCommand
        {
            Target = new GitRepoTarget(
                repoUrl: $"magentic://{info.WorkflowName}",
                branch: "main"),
            Diff = new[]
            {
                new ChangeEdit
                {
                    Op = Domain.AI.SkillTraining.EditOp.Append,
                    Content = info.ReplanText,
                    Target = string.Empty
                }
            },
            Summary = BuildSummary(info),
            BlastRadius = BlastRadius.Medium,
            IsStateChange = true,
            SkillKey = $"magentic:{info.WorkflowName}"
        };

        Result<ChangeProposal> result;
        try
        {
            result = await _mediator.Send(command, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Magentic change-proposal submit threw: workflow={WorkflowId} version={PlanVersion}",
                info.WorkflowId,
                info.PlanVersion);
            return null;
        }

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Magentic change-proposal submit failed: workflow={WorkflowId} version={PlanVersion} message={Message}",
                info.WorkflowId,
                info.PlanVersion,
                result.Errors.FirstOrDefault());
            return null;
        }

        _logger.LogInformation(
            "Magentic replan routed to change proposal: workflow={WorkflowId} proposal={ProposalId}",
            info.WorkflowId,
            result.Value!.Id);

        return result.Value;
    }

    private static bool IsStateChange(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var verb in DefaultStateChangeVerbs)
        {
            if (text.Contains(verb, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string BuildSummary(MagenticReplanInfo info)
    {
        var preview = info.ReplanText.Length <= 120
            ? info.ReplanText
            : info.ReplanText.Substring(0, 120) + "…";
        return $"Magentic replan v{info.PlanVersion} ({info.WorkflowName}): {preview}";
    }
}

/// <summary>
/// Descriptor for a Magentic replan event, built by the event subscriber and
/// handed to <see cref="MagenticChangeProposalRouter"/>.
/// </summary>
public sealed record MagenticReplanInfo
{
    /// <summary>The workflow identifier the replan belongs to.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>The symbolic workflow name.</summary>
    public required string WorkflowName { get; init; }

    /// <summary>The monotonic plan version after the replan (initial plan = 1).</summary>
    public required int PlanVersion { get; init; }

    /// <summary>
    /// The replan text from the manager (Task Ledger after the replan). The
    /// router scans this for state-change verbs.
    /// </summary>
    public required string ReplanText { get; init; }
}
