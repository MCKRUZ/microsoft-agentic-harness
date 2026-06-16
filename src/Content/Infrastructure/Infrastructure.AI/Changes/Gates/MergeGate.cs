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
    /// <summary>
    /// Stable, scrubbed reason code recorded when the resolved
    /// <see cref="IChangeApplier"/> throws. Used in place of the raw exception
    /// message so that the resulting <see cref="GateResult.Reason"/> — which the
    /// orchestrator persists verbatim into the proposal's <c>History</c> and the
    /// <c>changes.jsonl</c> audit file — never carries credentials embedded in
    /// exception text. Appliers drive GitOps/IaC backends and cloud SDKs whose
    /// exceptions routinely embed request URLs with SAS tokens or query-string
    /// secrets. The full exception is always captured via structured logging.
    /// </summary>
    internal const string ApplierThrewReasonCode = "applier_threw";

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
            // Persist a stable scrubbed code plus the exception *type* only — never
            // ex.Message. This Reason is copied verbatim into GateDecision.Reason,
            // which is written to changes.jsonl and the proposal History returned to
            // callers. Appliers drive GitOps/IaC backends and cloud SDKs whose
            // exception text routinely embeds request URLs with SAS tokens or
            // query-string credentials. The full exception is captured above via
            // structured logging (correlatable by ProposalId/TargetKind).
            return GateResult.Fail($"{ApplierThrewReasonCode}: {ex.GetType().Name}");
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
