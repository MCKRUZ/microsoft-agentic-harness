using Application.AI.Common.Interfaces.Changes;
using Application.Common.Helpers;
using Domain.AI.Changes;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Changes;

/// <summary>
/// Shared <c>kill-switch → load → status guard → audit → transition + save → optional post-save</c>
/// skeleton for the <c>Approve</c>, <c>Reject</c>, and <c>Cancel</c> command
/// handlers. Each handler injects its handler-specific guard predicate,
/// <see cref="GateDecision"/> factory, target status, and an optional post-save
/// hook (Approve uses it to enqueue the proposal for the merge worker).
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Application.AI.Common.CQRS.Changes</c> rather than inside any
/// single handler's folder because all three handlers consume it. Kept as an
/// internal static helper instead of an abstract base because the handlers have
/// divergent dependencies (Approve additionally takes a dispatch queue) and
/// base-class ceremony would obscure that.
/// </para>
/// <para>
/// <b>Audit before save, always.</b> The durable hash-chained audit
/// (<see cref="IChangeAuditWriter"/>, backed by <c>changes.jsonl</c>) is appended
/// <em>before</em> the state change is persisted, mirroring
/// <c>ChangeProposalOrchestrator.TransitionAsync</c>. An append failure aborts the
/// decision, so a proposal can never advance un-audited and the store cannot
/// disagree with the audit chain about who decided what. This matters most on the
/// human-decision path: these commands are reachable over HTTP, the proposal
/// store's production default is in-process and dies with the host, so without
/// this append the only record of a reviewer's approval would evaporate on
/// restart. Reject and Cancel drive the proposal terminal and the orchestrator
/// early-returns on terminal proposals, so this is their <em>only</em>
/// opportunity to reach the audit chain under any timing.
/// </para>
/// <para>
/// The kill switch is checked here rather than per-handler so all three commands
/// honour it, as <c>ChangesConfig.Enabled</c> documents ("all CQRS commands fail
/// fast").
/// </para>
/// </remarks>
internal static class ChangeProposalCommandHelper
{
    /// <summary>
    /// Stable, scrubbed failure code returned when the durable audit append fails. The
    /// underlying exception (which may embed audit storage paths or IO detail) is logged, never
    /// returned — the caller sees only this code, and the shared HTTP mapper further replaces
    /// the body of a <see cref="ResultFailureType.General"/> failure with a generic line.
    /// </summary>
    public const string AuditAppendFailedCode = "change_proposal.audit_append_failed";

    /// <summary>
    /// Run the shared decision pipeline against an existing proposal.
    /// </summary>
    /// <param name="store">The proposal store to load + save from.</param>
    /// <param name="audit">The durable audit sink; appended to before the state change is persisted.</param>
    /// <param name="config">Application configuration; supplies the pipeline kill switch and the orchestrator mode recorded on the audit line.</param>
    /// <param name="logger">Logger for kill-switch and audit-failure diagnostics — configuration keys and exception detail go here, never into the returned <see cref="Result"/>.</param>
    /// <param name="proposalId">The id of the proposal to act on.</param>
    /// <param name="statusGuard">
    /// Returns a non-null <see cref="Result{T}"/> to short-circuit the pipeline
    /// (typically <c>Conflict</c> when the current status doesn't permit the
    /// decision, so HTTP layers can map it to 409), or <c>null</c> to proceed.
    /// The proposal is passed in so the guard can include its actual current
    /// status in the failure message.
    /// </param>
    /// <param name="decisionFactory">Builds the <see cref="GateDecision"/> recorded on the transition and written to the audit chain.</param>
    /// <param name="targetStatus">The status to transition the proposal into.</param>
    /// <param name="postSave">
    /// Optional follow-up after the transition is persisted. When non-null its
    /// result replaces the default <c>Success(transitioned)</c> return — used by
    /// Approve to enqueue the proposal for the merge worker.
    /// </param>
    /// <param name="cancellationToken">Cancellation token forwarded to the audit sink, store, and post-save hook.</param>
    public static async Task<Result<ChangeProposal>> ApplyDecisionAsync(
        IChangeProposalStore store,
        IChangeAuditWriter audit,
        IOptionsMonitor<AppConfig> config,
        ILogger logger,
        string proposalId,
        Func<ChangeProposal, Result<ChangeProposal>?> statusGuard,
        Func<GateDecision> decisionFactory,
        ChangeProposalStatus targetStatus,
        Func<ChangeProposal, CancellationToken, Task<Result<ChangeProposal>>>? postSave,
        CancellationToken cancellationToken)
    {
        var changesConfig = config.CurrentValue.AI.Changes;
        if (!changesConfig.Enabled)
        {
            // The configuration key lives in the log, not the response: the wire body must not
            // hand a caller the name of the switch that re-arms the pipeline.
            logger.LogWarning(
                "Rejected a change-proposal decision because the pipeline is disabled. " +
                "Set AppConfig:AI:Changes:Enabled = true to enable it.");
            return Result<ChangeProposal>.Forbidden("The change-proposal pipeline is disabled.");
        }

        var proposal = await store.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
        {
            // The caller-supplied id is deliberately not echoed — it is already in the caller's
            // own request line, so reflecting it back adds nothing but an echo surface.
            return Result<ChangeProposal>.NotFound("The requested change proposal was not found.");
        }

        var guard = statusGuard(proposal);
        if (guard is not null)
        {
            return guard;
        }

        var decision = decisionFactory();
        var mode = ParseMode(changesConfig.DefaultMode);
        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            await audit.AppendAsync(
                proposal, decision, proposal.SubmittedBy, mode, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: no audit line, no state change. A decision that cannot be recorded
            // must not take effect — the alternative is a merge nobody can attribute.
            logger.LogError(
                ex,
                "Durable audit append failed for change proposal {ProposalId} (gate {GateKey}, correlation {CorrelationId}); " +
                "the {TargetStatus} transition was abandoned and the proposal is unchanged.",
                proposal.Id,
                decision.GateKey,
                correlationId,
                targetStatus);
            return Result<ChangeProposal>.Fail(AuditAppendFailedCode);
        }

        var transitioned = proposal.TransitionTo(targetStatus, decision);
        await store.SaveAsync(transitioned, cancellationToken).ConfigureAwait(false);

        if (postSave is not null)
        {
            return await postSave(transitioned, cancellationToken).ConfigureAwait(false);
        }
        return Result<ChangeProposal>.Success(transitioned);
    }

    /// <summary>
    /// Resolves the configured orchestrator mode for the audit line, defaulting to
    /// <see cref="OrchestratorMode.Shadow"/> on an unparseable value — identical to
    /// <c>ChangeProposalBackgroundService.ParseMode</c>, so a human decision and the
    /// orchestrator-driven transitions around it record the same mode.
    /// </summary>
    // Name-only. This is the highest-consequence parse in the harness: MergeGate short-circuits on
    // `context.Mode == Shadow`, so ANY value that is not exactly Shadow performs a real apply. A bare
    // Enum.TryParse accepted "99" and "Shadow,Enforce", neither of which equals Shadow — turning a
    // dry run into production writes off a config typo, while the documented fallback for an
    // unparseable value is Shadow precisely to prevent that.
    private static OrchestratorMode ParseMode(string raw) =>
        EnumNameHelper.TryParseName<OrchestratorMode>(raw, out var mode) ? mode : OrchestratorMode.Shadow;
}
