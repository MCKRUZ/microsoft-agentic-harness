using System.Text.Json.Serialization;
using Domain.AI.Runs;

namespace Presentation.ExecutionApi.Streaming;

/// <summary>
/// Base type for the frames the workflow progress endpoint writes as Server-Sent-Events.
/// </summary>
/// <remarks>
/// <para>
/// A small vocabulary, separate from the bundle stream's. That one reproduces the AG-UI subset a
/// conversational run needs — assistant text, deltas, message boundaries — and a workflow emits none
/// of it. Sharing a type here would mean a client parsing frames its feed can never contain.
/// </para>
/// <para>
/// Frames MUST be serialized against this base type: <see cref="JsonPolymorphicAttribute"/> emits the
/// <c>type</c> discriminator only then, and serializing a derived type directly drops it silently.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkflowProgressSnapshotEvent), "SNAPSHOT")]
[JsonDerivedType(typeof(WorkflowProgressStepEvent), "STEP")]
[JsonDerivedType(typeof(WorkflowProgressFinishedEvent), "FINISHED")]
[JsonDerivedType(typeof(WorkflowProgressGapEvent), "GAP")]
public abstract record WorkflowProgressEvent;

/// <summary>
/// First frame on every stream: where the run already is.
/// </summary>
/// <remarks>
/// Sent because a watcher almost never arrives at the instant a run starts, and nothing is buffered
/// for a watcher who has not arrived. Without it, a client that connected a second late would see an
/// apparently idle run until the next step happened to finish — or, for a run that had already ended,
/// nothing at all.
/// </remarks>
public sealed record WorkflowProgressSnapshotEvent(
    /// <summary>The run being watched.</summary>
    [property: JsonPropertyName("jobId")] string JobId,
    /// <summary>The workflow the run belongs to.</summary>
    [property: JsonPropertyName("workflowId")] string WorkflowId,
    /// <summary>Where the run had got to when the stream opened.</summary>
    [property: JsonPropertyName("status")] string Status,
    /// <summary>Whether the run had already finished, in which case no further frames follow.</summary>
    [property: JsonPropertyName("isTerminal")] bool IsTerminal) : WorkflowProgressEvent;

/// <summary>A step started or finished.</summary>
public sealed record WorkflowProgressStepEvent(
    /// <summary>Position in this run's event order. Gaps mean events were dropped.</summary>
    [property: JsonPropertyName("sequence")] long Sequence,
    /// <summary>When it happened.</summary>
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    /// <summary>Identifier of the step.</summary>
    [property: JsonPropertyName("stepId")] string? StepId,
    /// <summary>Human-readable name of the step, on the frame that starts it.</summary>
    [property: JsonPropertyName("stepName")] string? StepName,
    /// <summary>Where the step has got to.</summary>
    [property: JsonPropertyName("status")] string? Status) : WorkflowProgressEvent;

/// <summary>Last frame on the stream: the run reached a terminal state.</summary>
public sealed record WorkflowProgressFinishedEvent(
    /// <summary>Position in this run's event order.</summary>
    [property: JsonPropertyName("sequence")] long Sequence,
    /// <summary>When the run finished.</summary>
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    /// <summary>The terminal status.</summary>
    [property: JsonPropertyName("status")] string? Status,
    /// <summary>Caller-safe reason, when the run did not simply succeed.</summary>
    [property: JsonPropertyName("detail")] string? Detail) : WorkflowProgressEvent;

/// <summary>
/// The client fell behind and events were dropped.
/// </summary>
/// <remarks>
/// Sent rather than swallowed. A watcher that cannot keep up loses the oldest events so it cannot slow
/// the run down — but a feed that hid that would leave the client believing it had seen the whole run,
/// which is a worse failure than admitting the gap.
/// </remarks>
public sealed record WorkflowProgressGapEvent(
    /// <summary>How many events have been dropped for this watcher so far.</summary>
    [property: JsonPropertyName("droppedCount")] long DroppedCount) : WorkflowProgressEvent;

/// <summary>Projects substrate progress events onto the caller-visible frames.</summary>
internal static class WorkflowProgressEventMapper
{
    /// <summary>Maps one substrate event, or <see langword="null"/> for kinds the wire does not carry.</summary>
    internal static WorkflowProgressEvent? ToFrame(RunProgressEvent evt) => evt.Kind switch
    {
        RunProgressKind.StepStarted or RunProgressKind.StepCompleted =>
            new WorkflowProgressStepEvent(evt.Sequence, evt.OccurredAt, evt.StepId, evt.StepName, evt.Status),

        RunProgressKind.RunFinished =>
            new WorkflowProgressFinishedEvent(evt.Sequence, evt.OccurredAt, evt.Status, evt.Detail),

        // RunStarted is not forwarded: the snapshot frame that opens every stream already says the run
        // is running, and a second frame saying so would only ever be redundant or contradictory.
        _ => null
    };
}
