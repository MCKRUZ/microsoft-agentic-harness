using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.Traces;

namespace Infrastructure.AI.Memory;

/// <summary>
/// Filesystem-backed implementation of <see cref="IAgentHistoryStore"/> that writes
/// <see cref="AgentDecisionEvent"/> records to <c>decisions.jsonl</c> in the trace run directory.
/// </summary>
/// <remarks>
/// <para>
/// Uses its own <see cref="SemaphoreSlim"/> for <c>decisions.jsonl</c> — separate from the
/// <see cref="ITraceWriter"/>'s internal lock for <c>traces.jsonl</c>. The two files never
/// contend with each other, keeping append throughput higher.
/// </para>
/// <para>
/// <see cref="AgentDecisionEvent.Sequence"/> is assigned via <see cref="Interlocked.Increment"/>
/// before the semaphore is acquired, so sequence number allocation is lock-free while JSONL
/// write ordering is serialized.
/// </para>
/// <para>
/// Scoped per execution run — one instance per <see cref="ITraceWriter"/>. Created by
/// <c>AgentExecutionContextFactory</c> alongside the writer, not by the DI container directly.
/// Implements <see cref="IDisposable"/> — dispose alongside the paired <see cref="ITraceWriter"/>
/// to release the internal <see cref="SemaphoreSlim"/>.
/// </para>
/// </remarks>
public sealed class JsonlAgentHistoryStore : IAgentHistoryStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string _decisionsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _sequenceCounter;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonlAgentHistoryStore"/>.
    /// </summary>
    /// <param name="traceWriter">
    /// The scoped trace writer for this execution run. Provides the run directory path.
    /// </param>
    public JsonlAgentHistoryStore(ITraceWriter traceWriter)
    {
        _decisionsPath = Path.Combine(traceWriter.RunDirectory, "decisions.jsonl");
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <inheritdoc />
    public async Task AppendAsync(AgentDecisionEvent evt, CancellationToken cancellationToken = default)
    {
        // Assign sequence number lock-free; write ordering is serialized by the semaphore.
        var seq = Interlocked.Increment(ref _sequenceCounter);

        var finalEvent = evt with
        {
            Sequence = seq,
            Timestamp = evt.Timestamp == default ? DateTimeOffset.UtcNow : evt.Timestamp
        };

        var line = JsonSerializer.Serialize(finalEvent, SerializeOptions) + "\n";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_decisionsPath, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentDecisionEvent> QueryAsync(
        DecisionLogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_decisionsPath))
            yield break;

        var yielded = 0;

        using var stream = new FileStream(
            _decisionsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && yielded < query.Limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            AgentDecisionEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AgentDecisionEvent>(line, DeserializeOptions);
            }
            catch (JsonException)
            {
                // Corrupted line — skip
                continue;
            }

            if (evt is null) continue;

            if (!string.Equals(evt.ExecutionRunId, query.ExecutionRunId, StringComparison.Ordinal))
                continue;
            if (query.EventType is not null &&
                !string.Equals(evt.EventType, query.EventType, StringComparison.Ordinal))
                continue;
            if (query.ToolName is not null &&
                !string.Equals(evt.ToolName, query.ToolName, StringComparison.Ordinal))
                continue;
            if (query.TurnId is not null &&
                !string.Equals(evt.TurnId, query.TurnId, StringComparison.Ordinal))
                continue;
            if (evt.Sequence <= query.Since)
                continue;

            yield return evt;
            yielded++;
        }
    }
}
