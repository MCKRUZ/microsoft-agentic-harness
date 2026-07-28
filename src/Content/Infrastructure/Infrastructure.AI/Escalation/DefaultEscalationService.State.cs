using Domain.AI.Escalation;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// The per-escalation mutable state held by <see cref="DefaultEscalationService"/> while an
/// escalation is active, split out from the main partial so the orchestration logic and the state
/// shape it guards can be read independently.
/// </summary>
public sealed partial class DefaultEscalationService
{
	/// <summary>Tracks the mutable state of an active escalation.</summary>
	private sealed class EscalationState
	{
		public required EscalationRequest Request { get; init; }
		public List<ApproverDecision> Decisions { get; } = [];
		public TaskCompletionSource<EscalationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public required CancellationTokenSource TimeoutCts { get; init; }
		public required DateTimeOffset CreatedAt { get; init; }
		public bool IsResolved { get; set; }

		/// <summary>
		/// True when this state was restored from the durable store rather than created in
		/// this process.
		/// </summary>
		public bool WasRehydrated { get; init; }

		/// <summary>
		/// True when this state was rehydrated with its deadline ALREADY elapsed. Evaluated
		/// once, at restore, and never re-derived: re-testing the deadline after the timeout
		/// delay returns would let timer granularity or a backwards clock step report the
		/// deadline as still in the future, skipping the fail-closed downgrade of
		/// <c>TimeoutAction.Approve</c> and auto-approving an escalation no approver was
		/// re-notified about. See <c>ResolveTimeoutApproval</c>.
		/// </summary>
		public bool ExpiredDuringDowntime { get; init; }

		/// <summary>
		/// The resolved outcome, stashed when <see cref="IsResolved"/> flips true so the
		/// reconciler can re-drive a resolution whose fail-closed durable/audit writes faulted.
		/// Null while the escalation is genuinely pending.
		/// </summary>
		public EscalationOutcome? PendingOutcome { get; set; }

		/// <summary>
		/// True while some path owns finalization of this resolution. Set in the SAME lock
		/// block that sets <see cref="IsResolved"/>, so a reconcile pass can never claim a
		/// resolution the live path is still driving — the window that otherwise lets a pass
		/// finalize and notify while the live path then rolls back and tells the approver their
		/// vote was NOT recorded. Cleared on rollback and on each resolution failure branch, so
		/// a genuinely stuck state becomes claimable.
		/// </summary>
		public bool FinalizeClaimed { get; set; }

		/// <summary>
		/// True once a fail-closed durable or audit write failed during resolution, leaving the
		/// escalation resolved-but-unrecorded. Distinguishes "parked awaiting reconciliation"
		/// from a transient mid-resolution race, so a late decision gets an honest answer.
		/// </summary>
		public bool ResolutionFailed { get; set; }

		/// <summary>
		/// Serializes decision mutation plus its durable write for this escalation, making the
		/// snapshot-to-persist step atomic against concurrent submissions.
		/// </summary>
		public SemaphoreSlim WriteGate { get; } = new(1, 1);

		public readonly object Lock = new();

		/// <summary>
		/// Releases both disposable primitives this state owns. Every teardown path must call
		/// this — disposing only <see cref="TimeoutCts"/> leaks the <see cref="WriteGate"/>'s
		/// wait handle for the life of the process.
		/// </summary>
		public void DisposeSynchronizationPrimitives()
		{
			TimeoutCts.Dispose();
			WriteGate.Dispose();
		}
	}
}
