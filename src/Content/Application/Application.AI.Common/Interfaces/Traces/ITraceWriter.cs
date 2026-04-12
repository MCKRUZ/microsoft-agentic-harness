using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.Traces;

/// <summary>
/// Scoped writer for a single execution run. One instance per <see cref="TraceScope.ExecutionRunId"/>.
/// All methods are thread-safe. Sequence numbers are guaranteed monotonically increasing across
/// concurrent callers via <see cref="System.Threading.Interlocked.Increment(ref long)"/>.
/// Implements <see cref="IAsyncDisposable"/> — call <c>DisposeAsync</c> after <see cref="CompleteAsync"/>.
/// </summary>
public interface ITraceWriter : IAsyncDisposable
{
    /// <summary>
    /// Key used to store the <see cref="ITraceWriter"/> instance in
    /// <c>AgentExecutionContext.AdditionalProperties</c>.
    /// </summary>
    public const string AdditionalPropertiesKey = "__traceWriter";
    /// <summary>The scope this writer was created for.</summary>
    TraceScope Scope { get; }

    /// <summary>Absolute path to the run directory on disk.</summary>
    string RunDirectory { get; }

    /// <summary>
    /// Writes all turn artifacts into <c>turns/{turnNumber}/</c>.
    /// Applies <c>ISecretRedactor</c> to <c>SystemPrompt</c> before writing.
    /// Large tool results are written to <c>tool_results/{callId}.json</c>.
    /// </summary>
    Task WriteTurnAsync(int turnNumber, TurnArtifacts artifacts, CancellationToken ct = default);

    /// <summary>
    /// Appends one record to <c>traces.jsonl</c>. Thread-safe via an internal
    /// <see cref="System.Threading.SemaphoreSlim"/>. Sequence number is assigned by the writer.
    /// <c>ISecretRedactor</c> is applied to <c>PayloadSummary</c> before writing.
    /// <c>PayloadSummary</c> is truncated to 500 characters if it exceeds that length.
    /// </summary>
    Task AppendTraceAsync(ExecutionTraceRecord record, CancellationToken ct = default);

    /// <summary>Atomically writes <c>scores.json</c> (temp + rename).</summary>
    Task WriteScoresAsync(HarnessScores scores, CancellationToken ct = default);

    /// <summary>Atomically writes <c>summary.md</c> (temp + rename).</summary>
    Task WriteSummaryAsync(string markdown, CancellationToken ct = default);

    /// <summary>
    /// Finalizes the run by writing <c>write_completed: true</c> to <c>manifest.json</c>
    /// atomically. Should be called exactly once per writer instance.
    /// </summary>
    Task CompleteAsync(CancellationToken ct = default);
}
