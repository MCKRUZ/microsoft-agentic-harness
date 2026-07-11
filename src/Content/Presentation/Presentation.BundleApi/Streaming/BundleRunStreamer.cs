using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services;
using Domain.AI.Bundles;

namespace Presentation.BundleApi.Streaming;

/// <summary>
/// Drives one bundle run as a live Server-Sent-Events feed: it emits the run-lifecycle events, arms the ambient
/// assistant-text sink so tokens stream to the client as the agent generates them, and calls the shared
/// <see cref="IBundleRunExecutor"/> to run the conversation under its capability envelope and overlay.
/// </summary>
/// <remarks>
/// <para>
/// The executor is what arms the security ambients (envelope + overlay) around the run; this type adds only the
/// transport concern — translating the agent's text deltas into AG-UI <c>TEXT_MESSAGE_CONTENT</c> frames. The
/// sink is armed <em>around</em> the executor call and cleared in a <c>finally</c> so it can never leak onto a
/// later request on the same thread, and because the executor materialises the whole run before returning, the
/// sink stays armed for the entire stream.
/// </para>
/// <para>
/// A cancelled connection surfaces as an <see cref="OperationCanceledException"/> from the executor: the sink is
/// cleared and the exception propagates (the client is already gone), while the executor has already recorded
/// the run as cancelled. Every non-cancelled path ends with exactly one terminal frame —
/// <see cref="BundleRunFinishedEvent"/> on success or <see cref="BundleRunErrorEvent"/> otherwise.
/// </para>
/// </remarks>
public sealed class BundleRunStreamer
{
    private readonly IBundleRunExecutor _executor;

    /// <summary>Initializes a new <see cref="BundleRunStreamer"/>.</summary>
    public BundleRunStreamer(IBundleRunExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
    }

    /// <summary>
    /// Streams the run identified by <paramref name="record"/> to <paramref name="writer"/>. The caller must
    /// have already verified the requesting principal owns the run and set the SSE response headers.
    /// </summary>
    /// <param name="record">The queued run to drive (its handle is the stream's thread id, its job id the run id).</param>
    /// <param name="writer">The SSE writer targeting the client's response stream.</param>
    /// <param name="cancellationToken">The connection's abort token — cancelling it ends the run.</param>
    public async Task StreamAsync(
        BundleRunRecord record, BundleStreamEventWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(writer);

        var threadId = record.Handle;
        var runId = record.JobId;
        var messageId = Guid.NewGuid().ToString("N");

        await writer.WriteAsync(new BundleRunStartedEvent(threadId, runId), cancellationToken).ConfigureAwait(false);

        // Lazily open the assistant message on the first real delta, so a run that emits no text produces no
        // stray empty message. Deltas arrive strictly sequentially (one turn's stream at a time), so the flag
        // needs no synchronisation.
        var textStarted = false;
        var previousSink = AgentTurnStreamSink.Current;
        AgentTurnStreamSink.Current = new AgentTurnStreamSink(async (delta, ct) =>
        {
            if (!textStarted)
            {
                textStarted = true;
                await writer.WriteAsync(new BundleTextMessageStartEvent(messageId, "assistant"), ct).ConfigureAwait(false);
            }

            await writer.WriteAsync(new BundleTextMessageContentEvent(messageId, delta), ct).ConfigureAwait(false);
        });

        BundleRunExecution execution;
        try
        {
            execution = await _executor.ExecuteAsync(record.JobId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Never let the sink outlive this request, on any path (success, failure, or cancellation).
            AgentTurnStreamSink.Current = previousSink;
        }

        if (textStarted)
            await writer.WriteAsync(new BundleTextMessageEndEvent(messageId), cancellationToken).ConfigureAwait(false);

        if (IsSuccessful(execution))
            await writer.WriteAsync(new BundleRunFinishedEvent(threadId, runId), cancellationToken).ConfigureAwait(false);
        else
            await writer.WriteAsync(new BundleRunErrorEvent(DescribeFailure(execution)), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSuccessful(BundleRunExecution execution) =>
        execution.Status == BundleRunExecutionStatus.Ran
        && execution.Record is { Status: BundleRunStatus.Succeeded, Outcome.ConversationSucceeded: true };

    /// <summary>Maps a non-success execution to a caller-safe message — never a raw exception or internal code.</summary>
    private static string DescribeFailure(BundleRunExecution execution) => execution.Status switch
    {
        BundleRunExecutionStatus.NotFound => "The run could not be found. Start it again to obtain a new job id.",
        BundleRunExecutionStatus.AlreadyClaimed =>
            "The run is already being streamed or has already completed.",
        // Ran but not a clean success: a handle that expired before the run started (never stamped a start
        // time) vs. an agent run that started and then failed to complete successfully.
        _ => execution.Record?.StartedAt is null
            ? "The bundle handle expired before the run could start."
            : "The agent run did not complete successfully.",
    };
}
