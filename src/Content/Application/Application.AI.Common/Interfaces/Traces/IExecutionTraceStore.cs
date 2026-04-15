using Domain.Common.MetaHarness;

namespace Application.AI.Common.Interfaces.Traces;

/// <summary>
/// Singleton store that creates per-run <see cref="ITraceWriter"/> instances.
/// The store holds no per-run state — all run state lives in the writer.
/// </summary>
public interface IExecutionTraceStore
{
    /// <summary>
    /// Creates the run directory, writes the initial <c>manifest.json</c>, and returns
    /// a scoped <see cref="ITraceWriter"/>. Callers must call
    /// <see cref="ITraceWriter.CompleteAsync"/> when the run is finished.
    /// </summary>
    Task<ITraceWriter> StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Returns the absolute directory path for a given scope without creating it.
    /// Used by the proposer to locate trace directories for filesystem navigation.
    /// </summary>
    Task<string> GetRunDirectoryAsync(TraceScope scope, CancellationToken ct = default);
}
