using System.Collections.Concurrent;
using Domain.Common.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that maintains a fixed-size circular buffer of
/// recent <see cref="LogEntry"/> objects in memory. Enables diagnostics endpoints
/// and debugging UIs to access recent log history without file I/O.
/// </summary>
/// <remarks>
/// The buffer capacity is configured via <c>AppConfig.Logging.RingBufferCapacity</c>
/// (default: 500). When the buffer is full, the oldest entry is silently discarded.
/// <para>
/// This provider is thread-safe and lock-free for writes. Reads via
/// <see cref="GetEntries"/> return a snapshot — iteration is safe during concurrent writes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // In a diagnostics endpoint:
/// app.MapGet("/api/diagnostics/logs", (InMemoryRingBufferLoggerProvider provider) =>
///     provider.GetEntries());
///
/// // Filtered query:
/// var errors = provider.GetEntries()
///     .Where(e => e.Level >= LogLevel.Error)
///     .Where(e => e.AgentId == "planner");
/// </code>
/// </example>
public sealed class InMemoryRingBufferLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, InMemoryRingBufferLogger> _loggers = new();
    private readonly LogEntry[] _buffer;
    private readonly IExternalScopeProvider? _scopeProvider;
    private int _head;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryRingBufferLoggerProvider"/> class.
    /// </summary>
    /// <param name="config">Application configuration for buffer capacity.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context extraction.</param>
    public InMemoryRingBufferLoggerProvider(
        IOptionsMonitor<AppConfig> config,
        IExternalScopeProvider? scopeProvider = null)
    {
        var capacity = Math.Max(10, config.CurrentValue.Logging.RingBufferCapacity);
        _buffer = new LogEntry[capacity];
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name =>
            new InMemoryRingBufferLogger(name, this, _scopeProvider));

    /// <summary>
    /// Adds a log entry to the ring buffer. If the buffer is full,
    /// the oldest entry is overwritten.
    /// </summary>
    /// <param name="entry">The log entry to add.</param>
    internal void AddEntry(LogEntry entry)
    {
        var index = (uint)Interlocked.Increment(ref _head) - 1;
        var capacity = (uint)_buffer.Length;
        _buffer[(int)(index % capacity)] = entry;

        // Track count up to capacity
        int currentCount;
        do
        {
            currentCount = _count;
            if (currentCount >= (int)capacity)
                break;
        } while (Interlocked.CompareExchange(ref _count, currentCount + 1, currentCount) != currentCount);
    }

    /// <summary>
    /// Returns a snapshot of all entries currently in the buffer, ordered from
    /// oldest to newest.
    /// </summary>
    /// <returns>An enumerable of <see cref="LogEntry"/> objects.</returns>
    public IReadOnlyList<LogEntry> GetEntries()
    {
        var capacity = (uint)_buffer.Length;
        var head = (uint)_head;
        var count = (uint)Math.Min(head, (int)capacity);

        var result = new List<LogEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var entry = _buffer[(int)((head - count + i) % capacity)];
            if (entry is not null)
                result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Clears all entries from the ring buffer.
    /// Not thread-safe with concurrent writes — call only when logging is paused.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_buffer);
        Interlocked.Exchange(ref _head, 0);
        Interlocked.Exchange(ref _count, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources — buffer is GC-collected
    }
}
