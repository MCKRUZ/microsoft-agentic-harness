using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Agents;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Traces;

/// <summary>
/// Finalizes and disposes the execution-trace writer an <see cref="AgentExecutionContext"/> may be
/// carrying, when execution tracing is on (<c>MetaHarness.ExecutionTracingEnabled</c>). Shared by every
/// caller that builds an agent via <see cref="IAgentFactory.CreateAgentWithContextFromSkillsAsync"/>
/// and is therefore responsible for the trace writer <c>AgentExecutionContextFactory.StartTraceRunAsync</c>
/// stashed into the context — a context nobody completes leaks an open file handle and semaphore per
/// agent build, and leaves its run manifest permanently flagged incomplete.
/// </summary>
/// <remarks>
/// Originally private to <c>AgentConversationCache</c>, which finalizes on its own cache entries'
/// eviction. Extracted so <c>MagenticAgentTurnRunner</c> — which builds agents directly through
/// <see cref="IAgentFactory"/> rather than through the per-conversation cache, and so never gets a
/// free eviction hook — can finalize each agent it builds itself, immediately after the turn that
/// used it completes, instead of duplicating this logic or leaking every Magentic turn's trace
/// writers with no cleanup path at all.
/// </remarks>
public static class ExecutionTraceWriterCleanup
{
    /// <summary>
    /// Completes and disposes <paramref name="context"/>'s trace writer, if it has one. A no-op when
    /// <paramref name="context"/> is null, carries no trace writer, or tracing was never enabled for
    /// this build.
    /// </summary>
    /// <remarks>
    /// Failure is contained rather than propagated, and logged rather than hidden — an error finalizing
    /// or disposing a trace writer must never fail the turn whose result it is bookkeeping for, but a
    /// run whose manifest was never stamped complete is otherwise indistinguishable on disk from one
    /// still in flight, so silence is not an option either.
    /// </remarks>
    public static async Task CompleteAsync(
        AgentExecutionContext? context, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (context?.AdditionalProperties is null
            || !context.AdditionalProperties.TryGetValue(
                ITraceWriter.AdditionalPropertiesKey, out var stashed)
            || stashed is not ITraceWriter writer)
        {
            return;
        }

        try
        {
            await writer.CompleteAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Failed to finalize the execution trace for run {ExecutionRunId} — its manifest "
                + "will stay marked incomplete.",
                writer.Scope.ExecutionRunId);
        }
        finally
        {
            try
            {
                await writer.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to dispose the execution trace writer for run {ExecutionRunId} — its "
                    + "file handle and semaphore may not have been released.",
                    writer.Scope.ExecutionRunId);
            }
        }
    }
}
