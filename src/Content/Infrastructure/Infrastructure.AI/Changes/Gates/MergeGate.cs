using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Changes.Gates;

/// <summary>
/// The only mutator in the pipeline. Resolves an <see cref="IChangeApplier"/>
/// keyed by <see cref="ChangeTarget.Kind"/> and invokes it; turns the
/// resulting <see cref="ChangeApplyResult"/> into a <see cref="GateResult"/>.
/// In <see cref="OrchestratorMode.Shadow"/> short-circuits before invoking
/// the applier — gates still audit-log, but nothing in the world changes.
/// </summary>
/// <remarks>
/// <para>
/// Shadow-mode short-circuit is the whole point of having OrchestratorMode at
/// all. An operator can run the pipeline against real proposals for days,
/// watch the audit, tune gates, and only then flip to Live per skill. Every
/// applier MUST honor this convention by checking the gate's context — but
/// MergeGate enforces the rule centrally so a buggy applier can't slip a
/// mutation through in Shadow mode.
/// </para>
/// <para>
/// Missing applier → <see cref="GateAction.Fail"/> with a directive message;
/// applier exceptions caught and turned into Fail so the orchestrator never
/// sees an unhandled throw from the merge phase. The orchestrator turns Fail
/// into a terminal Rejected — never partial-merge.
/// </para>
/// <para>
/// Successful merge logs structured info (proposal id, target, application
/// reference). PR-2 does not yet emit a domain event for downstream consumers
/// (drift detection, learnings, KG memory) — that wiring lands when those
/// consumers actually exist in the harness.
/// </para>
/// </remarks>
public sealed class MergeGate : IChangeProposalGate
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MergeGate> _logger;

    /// <summary>Initializes a new <see cref="MergeGate"/>.</summary>
    public MergeGate(IServiceProvider services, ILogger<MergeGate> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => WellKnownGateKeys.Merge;

    /// <inheritdoc />
    public GatePhase Phase => GatePhase.Merge;

    /// <inheritdoc />
    public async Task<GateResult> EvaluateAsync(
        ChangeProposal proposal,
        GateContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        // Shadow-mode short-circuit. Audited as a Pass with the "shadow" marker;
        // the proposal still drives to Merged so the lifecycle is observable.
        if (context.Mode == OrchestratorMode.Shadow)
        {
            _logger.LogInformation(
                "MergeGate shadow-mode short-circuit for proposal {ProposalId} ({TargetKind}).",
                proposal.Id,
                proposal.Target.Kind);
            return GateResult.Pass($"shadow mode — no real apply (target {proposal.Target.Kind})");
        }

        var applier = _services.GetKeyedService<IChangeApplier>(proposal.Target.Kind);
        if (applier is null)
        {
            return GateResult.Fail(
                $"No IChangeApplier registered for target kind '{proposal.Target.Kind}'. " +
                $"Register one via services.AddKeyedSingleton<IChangeApplier>(ChangeTargetKind.{proposal.Target.Kind}, ...).");
        }

        ChangeApplyResult result;
        try
        {
            result = await applier.ApplyAsync(proposal, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MergeGate applier threw for proposal {ProposalId} ({TargetKind}).",
                proposal.Id,
                proposal.Target.Kind);
            return GateResult.Fail($"applier threw: {ex.GetType().Name}: {ex.Message}");
        }

        if (!result.Success)
        {
            return GateResult.Fail(
                $"merge failed: {result.Reason}",
                result.EvidenceHash);
        }

        _logger.LogInformation(
            "MergeGate applied proposal {ProposalId} to {TargetKind}; reference {ApplicationReference}.",
            proposal.Id,
            proposal.Target.Kind,
            result.ApplicationReference);

        return GateResult.Pass(
            string.IsNullOrEmpty(result.Reason)
                ? $"applied (reference: {result.ApplicationReference})"
                : $"{result.Reason} (reference: {result.ApplicationReference})",
            result.EvidenceHash);
    }
}
