namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// Telemetry names for the in-process queues that hand accepted work to a background dispatcher, and for
/// the live progress streams that follow a dispatched workflow run.
/// </summary>
/// <remarks>
/// These answer the two questions a host under load cannot answer from request metrics alone: how much
/// accepted work is waiting for a free dispatch slot, and how long it waited. A request that returns 202 in
/// 20 ms says nothing about a run that then sits in the queue for a minute.
/// </remarks>
public static class DispatchConventions
{
    // ── Attribute names ──

    /// <summary>
    /// Which dispatch queue a measurement belongs to. Values: <see cref="BundleRunQueue"/>,
    /// <see cref="WorkflowRunQueue"/>, <see cref="ChangeProposalQueue"/>.
    /// </summary>
    public const string Queue = "agent.dispatch.queue";

    // ── Attribute values for Queue ──

    /// <summary>Poll-only bundle runs waiting for the bundle-run dispatcher. Streamed runs are never queued.</summary>
    public const string BundleRunQueue = "bundle_run";

    /// <summary>Submitted workflow runs waiting for the workflow-run dispatcher.</summary>
    public const string WorkflowRunQueue = "workflow_run";

    /// <summary>Approved change proposals waiting for the change-proposal dispatcher.</summary>
    public const string ChangeProposalQueue = "change_proposal";

    // ── Metric names ──

    /// <summary>Items accepted into a dispatch queue and not yet taken by its dispatcher. Tags: queue.</summary>
    public const string QueueDepth = "agent.dispatch.queue_depth";

    /// <summary>
    /// Milliseconds from an item entering a dispatch queue to its dispatcher taking it. Tags: queue.
    /// </summary>
    /// <remarks>
    /// The dispatcher takes an item only when one of its slots is free, so this is the time spent waiting
    /// for capacity, not the time the work itself took.
    /// </remarks>
    public const string QueueWait = "agent.dispatch.queue_wait";

    /// <summary>
    /// Workflow progress streams open right now, counted when a subscription is granted and released when
    /// it is disposed.
    /// </summary>
    /// <remarks>
    /// Bundle-run streams need no instrument of their own: each holds a permit from the Execution API's
    /// stream concurrency limiter, which ASP.NET Core already reports as
    /// <c>aspnetcore.rate_limiting.active_request_leases</c>.
    /// </remarks>
    public const string ProgressStreamsActive = "agent.dispatch.progress_streams_active";
}
