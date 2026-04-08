using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Common.Config;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes log entries to run-based directories
/// with a background consumer thread. Each execution run gets its own directory
/// containing <c>log.txt</c> (structured), <c>console.txt</c> (human-readable),
/// and a JSON manifest summarizing the run.
/// </summary>
/// <remarks>
/// <para>
/// Run lifecycle: Call <see cref="StartNewRun"/> at the beginning of an execution,
/// <see cref="CompleteRun"/> at the end. Between these calls, all loggers created by
/// this provider enqueue messages to a bounded <see cref="BlockingCollection{T}"/>
/// (capacity 1000) which is drained by a dedicated background thread.
/// </para>
/// <para>
/// Directory structure:
/// <code>
/// {LogsBasePath}/
///   └── {RunId}/
///         ├── {Phase}/
///         │     ├── log.txt
///         │     └── console.txt
///         └── manifest.json
/// </code>
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly BlockingCollection<(string Structured, string Console)> _messageQueue = new(1000);
    private readonly IOptionsMonitor<LoggingConfig> _config;
    private readonly IExternalScopeProvider? _scopeProvider;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private StreamWriter? _structuredWriter;
    private StreamWriter? _consoleWriter;
    private Thread? _backgroundThread;
    private CancellationTokenSource? _cts;
    private string? _currentRunId;
    private int _logEntryCount;

    /// <summary>Gets whether a run is currently active and accepting log entries.</summary>
    public bool IsRunActive => _structuredWriter is not null;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLoggerProvider"/> class.
    /// </summary>
    /// <param name="config">Application configuration for resolving log paths.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public FileLoggerProvider(IOptionsMonitor<LoggingConfig> config, IExternalScopeProvider? scopeProvider = null)
    {
        _config = config;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this, _scopeProvider));

    /// <summary>
    /// Starts a new logging run, creating the directory structure and opening
    /// output files for writing.
    /// </summary>
    /// <param name="runId">Unique identifier for this run.</param>
    /// <param name="phase">Optional phase name for subdirectory organization.</param>
    public void StartNewRun(string runId, string? phase = null)
    {
        lock (_lock)
        {
            CloseCurrentRun();

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

            _structuredWriter = new StreamWriter(
                Path.Combine(runPath, "log.txt"), append: false);
            _consoleWriter = new StreamWriter(
                Path.Combine(runPath, "console.txt"), append: false);

            _currentRunId = runId;
            _logEntryCount = 0;
            _cts = new CancellationTokenSource();
            _backgroundThread = new Thread(ProcessMessageQueue)
            {
                IsBackground = true,
                Name = $"FileLogger-{runId}"
            };
            _backgroundThread.Start();
        }
    }

    /// <summary>
    /// Completes the current run, flushing pending messages and writing the
    /// run manifest to <c>manifest.json</c>.
    /// </summary>
    /// <param name="manifest">The run manifest to serialize.</param>
    public void CompleteRun(RunManifest? manifest = null)
    {
        lock (_lock)
        {
            FlushPendingMessages();

            if (manifest is not null && _currentRunId is not null)
            {
                var basePath = _config.CurrentValue.LogsBasePath;
                if (!string.IsNullOrWhiteSpace(basePath))
                {
                    var manifestWithCount = manifest with { LogEntryCount = _logEntryCount };
                    var manifestPath = Path.Combine(basePath, _currentRunId, "manifest.json");
                    var json = JsonSerializer.Serialize(manifestWithCount, ManifestJsonOptions);
                    File.WriteAllText(manifestPath, json);
                }
            }

            CloseCurrentRun();
        }
    }

    /// <summary>
    /// Enqueues a message pair for background writing. Non-blocking; drops
    /// messages if the queue is full (bounded at 1000 items).
    /// </summary>
    /// <param name="structured">The structured log line for <c>log.txt</c>.</param>
    /// <param name="console">The human-readable line for <c>console.txt</c>.</param>
    internal void WriteMessage(string structured, string console)
    {
        if (_messageQueue.TryAdd((structured, console)))
            Interlocked.Increment(ref _logEntryCount);
    }

    private void ProcessMessageQueue()
    {
        try
        {
            foreach (var (structured, console) in _messageQueue.GetConsumingEnumerable(_cts!.Token))
            {
                lock (_lock)
                {
                    try
                    {
                        _structuredWriter?.WriteLine(structured);
                        _consoleWriter?.WriteLine(console);

                        if (_messageQueue.Count == 0)
                        {
                            _structuredWriter?.Flush();
                            _consoleWriter?.Flush();
                        }
                    }
                    catch (IOException)
                    {
                        // File I/O failure — continue processing remaining messages
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown via CancellationToken
        }
    }

    private void FlushPendingMessages()
    {
        while (_messageQueue.TryTake(out var msg))
        {
            try
            {
                _structuredWriter?.WriteLine(msg.Structured);
                _consoleWriter?.WriteLine(msg.Console);
            }
            catch (IOException)
            {
                // Best-effort flush
            }
        }
    }

    private void CloseCurrentRun()
    {
        _cts?.Cancel();
        _backgroundThread?.Join(TimeSpan.FromSeconds(2));

        _structuredWriter?.Dispose();
        _consoleWriter?.Dispose();
        _structuredWriter = null;
        _consoleWriter = null;
        _currentRunId = null;
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
