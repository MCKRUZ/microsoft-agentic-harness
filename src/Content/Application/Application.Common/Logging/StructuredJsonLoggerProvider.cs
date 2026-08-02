using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes log entries as JSONL (one JSON object
/// per line) to a run-based output file. Enables machine-parseable log analysis for
/// agent session debugging, token accounting, and tool usage auditing.
/// </summary>
/// <remarks>
/// Follows the same run lifecycle pattern as <see cref="FileLoggerProvider"/>:
/// <see cref="StartNewRun"/> opens a <c>structured.jsonl</c> file,
/// <see cref="CompleteRun"/> flushes and closes it.
/// A background thread drains a bounded queue (capacity 1000).
/// </remarks>
public sealed class StructuredJsonLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, StructuredJsonLogger> _loggers = new();
    private readonly BlockingCollection<string> _messageQueue = new(1000);
    private readonly IOptionsMonitor<LoggingConfig> _config;
    private readonly IExternalScopeProvider? _scopeProvider;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private StreamWriter? _writer;
    private Thread? _backgroundThread;
    private CancellationTokenSource? _cts;

    /// <summary>Gets whether a run is currently active and accepting log entries.</summary>
    public bool IsRunActive => _writer is not null;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructuredJsonLoggerProvider"/> class.
    /// </summary>
    /// <param name="config">Application configuration for resolving log paths.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context extraction.</param>
    public StructuredJsonLoggerProvider(
        IOptionsMonitor<LoggingConfig> config,
        IExternalScopeProvider? scopeProvider = null)
    {
        _config = config;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new StructuredJsonLogger(name, this, _scopeProvider));

    /// <summary>
    /// Starts a new JSONL logging run, creating the output file.
    /// </summary>
    /// <param name="runId">Unique identifier for this run.</param>
    /// <param name="phase">Optional phase name for subdirectory organization.</param>
    public void StartNewRun(string runId, string? phase = null)
    {
        // Stop the previous run's drain thread BEFORE taking the lock — see StopDrainThread.
        StopDrainThread();

        lock (_lock)
        {
            // Drain anything the previous run left queued into the previous run's file. The queue is
            // shared across runs, so entries left behind here would otherwise be written into the NEXT
            // run's file and be attributed to the wrong run.
            FlushPendingMessages();
            CloseWriter();

            var basePath = _config.CurrentValue.LogsBasePath;
            if (string.IsNullOrWhiteSpace(basePath))
                return;

            var runPath = phase is not null
                ? Path.Combine(basePath, runId, phase)
                : Path.Combine(basePath, runId);

            var fullBase = Path.GetFullPath(basePath);
            var fullRun = Path.GetFullPath(runPath);
            if (!fullRun.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Resolved run path escapes the base log directory.");

            Directory.CreateDirectory(runPath);

            _writer = new StreamWriter(
                Path.Combine(runPath, "structured.jsonl"), append: false);

            _cts = new CancellationTokenSource();
            _backgroundThread = new Thread(ProcessMessageQueue)
            {
                IsBackground = true,
                Name = $"JsonLogger-{runId}"
            };
            _backgroundThread.Start();
        }
    }

    /// <summary>
    /// Completes the current JSONL run, flushing pending entries and closing the file.
    /// </summary>
    public void CompleteRun()
    {
        StopDrainThread();

        lock (_lock)
        {
            FlushPendingMessages();
            CloseWriter();
        }
    }

    /// <summary>
    /// Serializes and enqueues a JSON entry for background writing.
    /// </summary>
    /// <param name="entry">The dictionary of key-value pairs to serialize.</param>
    internal void WriteJsonEntry(Dictionary<string, object?> entry)
    {
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        _messageQueue.TryAdd(json);
    }

    private void ProcessMessageQueue()
    {
        try
        {
            foreach (var json in _messageQueue.GetConsumingEnumerable(_cts!.Token))
            {
                lock (_lock)
                {
                    try
                    {
                        _writer?.WriteLine(json);

                        if (_messageQueue.Count == 0)
                            _writer?.Flush();
                    }
                    catch (IOException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FlushPendingMessages()
    {
        while (_messageQueue.TryTake(out var json))
        {
            try { _writer?.WriteLine(json); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Signals the drain thread to stop and waits for it to finish the entry it may already have
    /// dequeued.
    /// </summary>
    /// <remarks>
    /// <b>Must be called WITHOUT holding <c>_lock</c>.</b> <see cref="ProcessMessageQueue"/> takes an
    /// entry off the queue and then acquires <c>_lock</c> to write it, so joining that thread while
    /// holding the lock deadlocks the two until the timeout elapses — and the writer is disposed
    /// immediately afterwards, silently dropping the in-flight entry. Cancelling the token only
    /// unblocks the queue wait; a thread that has already dequeued still writes its entry first, which
    /// is exactly why the join has to happen out here where it can actually make progress.
    /// </remarks>
    private void StopDrainThread()
    {
        Thread? thread;
        lock (_lock)
        {
            _cts?.Cancel();
            thread = _backgroundThread;
            _backgroundThread = null;
        }

        thread?.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Disposes the current run's writer and cancellation source. Callers hold <c>_lock</c> and must
    /// have already stopped the drain thread via <see cref="StopDrainThread"/>.
    /// </summary>
    private void CloseWriter()
    {
        _writer?.Dispose();
        _writer = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CompleteRun();
        _messageQueue.Dispose();
    }
}
