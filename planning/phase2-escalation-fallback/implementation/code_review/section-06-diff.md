diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationAuditStore.cs b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationAuditStore.cs
new file mode 100644
index 0000000..301c25c
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationAuditStore.cs
@@ -0,0 +1,31 @@
+using Domain.AI.Escalation;
+
+namespace Application.AI.Common.Interfaces.Escalation;
+
+/// <summary>
+/// Append-only audit store for escalation lifecycle events.
+/// Records requests, individual approver decisions, and final outcomes
+/// as <see cref="EscalationAuditRecord"/> entries for compliance.
+/// </summary>
+/// <remarks>
+/// The default implementation writes JSONL (one JSON object per line) with file
+/// locking, following the same pattern as <c>JsonlDelegationStore</c> from Phase 1.
+/// Each record includes a <c>RecordType</c> discriminator for deserialization.
+/// </remarks>
+public interface IEscalationAuditStore
+{
+    /// <summary>Records that an escalation was created.</summary>
+    Task RecordRequestAsync(EscalationRequest request, CancellationToken ct);
+
+    /// <summary>Records an individual approver's decision.</summary>
+    Task RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);
+
+    /// <summary>Records the final outcome of an escalation.</summary>
+    Task RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct);
+
+    /// <summary>
+    /// Returns the full audit history for a specific escalation, ordered chronologically.
+    /// Returns an empty list if the escalation ID is unknown.
+    /// </summary>
+    Task<IReadOnlyList<EscalationAuditRecord>> GetHistoryAsync(Guid escalationId, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotificationChannel.cs b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotificationChannel.cs
new file mode 100644
index 0000000..99e19dc
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotificationChannel.cs
@@ -0,0 +1,27 @@
+using Domain.AI.Escalation;
+
+namespace Application.AI.Common.Interfaces.Escalation;
+
+/// <summary>
+/// A single delivery channel for escalation notifications (e.g., AG-UI, Slack, Teams).
+/// </summary>
+/// <remarks>
+/// Implement this interface to add a new notification channel. Register the implementation
+/// as <c>IEscalationNotificationChannel</c> in DI -- the <c>CompositeEscalationNotifier</c>
+/// automatically discovers and fans out to all registered channels.
+/// <para>
+/// Implementations MUST be idempotent and MUST NOT throw exceptions that would block
+/// other channels. The composite catches and logs per-channel failures.
+/// </para>
+/// </remarks>
+public interface IEscalationNotificationChannel
+{
+    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
+    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);
+
+    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
+    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);
+
+    /// <summary>Warns approvers that an escalation is about to expire.</summary>
+    Task NotifyEscalationExpiringAsync(Guid escalationId, TimeSpan remaining, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotifier.cs b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotifier.cs
new file mode 100644
index 0000000..8aa7de4
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationNotifier.cs
@@ -0,0 +1,28 @@
+using Domain.AI.Escalation;
+
+namespace Application.AI.Common.Interfaces.Escalation;
+
+/// <summary>
+/// Delivers escalation notifications to human reviewers.
+/// </summary>
+/// <remarks>
+/// The default implementation (<c>CompositeEscalationNotifier</c>) fans out to all
+/// registered <see cref="IEscalationNotificationChannel"/> instances. Individual channel
+/// failures are caught and logged without blocking other channels.
+/// <para>
+/// This interface is the public contract consumed by <c>IEscalationService</c>.
+/// To add a new delivery channel, implement <see cref="IEscalationNotificationChannel"/>
+/// and register it in DI -- do NOT implement this interface directly.
+/// </para>
+/// </remarks>
+public interface IEscalationNotifier
+{
+    /// <summary>Notifies approvers that a new escalation requires their attention.</summary>
+    Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct);
+
+    /// <summary>Notifies interested parties that an escalation has been resolved.</summary>
+    Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct);
+
+    /// <summary>Warns approvers that an escalation is about to expire.</summary>
+    Task NotifyEscalationExpiringAsync(Guid escalationId, TimeSpan remaining, CancellationToken ct);
+}
diff --git a/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs
new file mode 100644
index 0000000..1ae9de0
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs
@@ -0,0 +1,45 @@
+using Domain.AI.Escalation;
+
+namespace Application.AI.Common.Interfaces.Escalation;
+
+/// <summary>
+/// Orchestrates the escalation lifecycle: creation, notification dispatch,
+/// approval tracking, timeout management, and outcome resolution.
+/// </summary>
+/// <remarks>
+/// Two consumption modes are supported:
+/// <list type="bullet">
+///   <item><description><see cref="RequestEscalationAsync"/> -- blocking; caller awaits the human decision.</description></item>
+///   <item><description><see cref="QueueEscalationAsync"/> -- non-blocking; returns an ID for later polling.</description></item>
+/// </list>
+/// The mode is selected by the agent's <c>EscalationWaitBehavior</c> (Block vs. QueueAndContinue),
+/// resolved from the autonomy tier policy.
+/// </remarks>
+public interface IEscalationService
+{
+    /// <summary>
+    /// Creates an escalation and blocks until a human decision resolves it or the timeout expires.
+    /// </summary>
+    Task<EscalationOutcome> RequestEscalationAsync(EscalationRequest request, CancellationToken ct);
+
+    /// <summary>
+    /// Creates an escalation without blocking. Returns the escalation ID for later polling.
+    /// </summary>
+    Task<Guid> QueueEscalationAsync(EscalationRequest request, CancellationToken ct);
+
+    /// <summary>
+    /// Submits an approver's decision. Returns the final outcome if this decision resolves
+    /// the escalation (per the approval strategy), or null if the escalation is still pending.
+    /// </summary>
+    Task<EscalationOutcome?> SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct);
+
+    /// <summary>
+    /// Returns the pending escalation request, or null if resolved or unknown.
+    /// </summary>
+    Task<EscalationRequest?> GetPendingEscalationAsync(Guid escalationId, CancellationToken ct);
+
+    /// <summary>
+    /// Returns all pending escalations assigned to a specific approver.
+    /// </summary>
+    Task<IReadOnlyList<EscalationRequest>> GetPendingEscalationsAsync(string approverName, CancellationToken ct);
+}
