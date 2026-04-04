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
    private readonly IOptionsMonitor<AppConfig> _config;
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
        IOptionsMonitor<AppConfig> config,
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
        lock (_lock)
        {
            CloseCurrentRun();

            var basePath = _config.CurrentValue.Logging.LogsBasePath;
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
        lock (_lock)
        {
            FlushPendingMessages();
            CloseCurrentRun();
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

    private void CloseCurrentRun()
    {
        _cts?.Cancel();
        _backgroundThread?.Join(TimeSpan.FromSeconds(2));
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
