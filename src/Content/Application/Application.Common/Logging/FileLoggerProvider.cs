using System.Collections.Concurrent;
using System.Text.Json;
using Application.Common.Interfaces.Common;
using Application.Common.Interfaces.Compliance;
using Domain.Common.Config;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

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
///         ├── log-run.json
///         └── manifest.json
/// </code>
/// A run nothing has written to for <c>Logging:Retention:RetainFor</c> is deleted (#901), except the run this provider
/// is writing; see <see cref="LogRunFolder"/>.
/// </para>
/// </remarks>
[PersonalDataStore(
    "Free-form log text in {Logging:LogsBasePath}/{runId}/log.txt and console.txt. A run is deleted once nothing has written to it for Logging:Retention (30 days by default), except the run in progress; runs from before the run marker existed are never swept, and a run another process is writing is protected only by its age. Only written once a run is started (StartNewRun), which nothing in production calls today. Redaction masks emails, phones and keys, not names or ids.",
    PersonalDataCategories.Identifiers | PersonalDataCategories.ConversationContent | PersonalDataCategories.ToolInputsAndOutputs | PersonalDataCategories.PeopleNames | PersonalDataCategories.NetworkTargets | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Configured,
    ErasureSupport.NotSupported,
    ExportSupport.NotSupported)]
public sealed class FileLoggerProvider : ILoggerProvider, IStoreRetentionPeriod
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly BlockingCollection<(string Structured, string Console)> _messageQueue = new(1000);
    private readonly IOptionsMonitor<LoggingConfig> _config;
    private readonly IExternalScopeProvider? _scopeProvider;
    private readonly IOwnerOnlyDirectoryCreator _directoryCreator;
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
    private string? _currentRunRoot;
    private int _logEntryCount;

    /// <summary>Gets whether a run is currently active and accepting log entries.</summary>
    public bool IsRunActive => _structuredWriter is not null;

    /// <inheritdoc />
    /// <remarks>A period below the sweep's floor is refused by the sweep, so it is reported as no period at all.</remarks>
    public TimeSpan? CurrentRetentionPeriod =>
        _config.CurrentValue.Retention is { Enabled: true } retention
        && retention.RetainFor >= RetentionPeriodConfig.MinimumRetainFor
            ? retention.RetainFor
            : null;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileLoggerProvider"/> class.
    /// </summary>
    /// <param name="config">Application configuration for resolving log paths.</param>
    /// <param name="directoryCreator">
    /// Creates the per-run log directory with owner-only permissions on POSIX (#672).
    /// </param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public FileLoggerProvider(
        IOptionsMonitor<LoggingConfig> config,
        IOwnerOnlyDirectoryCreator directoryCreator,
        IExternalScopeProvider? scopeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directoryCreator);
        _config = config;
        _directoryCreator = directoryCreator;
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

            var (runRoot, runPath) = LogRunFolder.Resolve(basePath, runId, phase);

            // Live before any file exists, so the retention sweep cannot delete the folder while it is being opened.
            LogRunFolder.BeginRun(runRoot);
            try
            {
                _directoryCreator.Create(runPath);
                LogRunFolder.WriteMarker(runRoot, runId);

                _structuredWriter = new StreamWriter(
                    Path.Combine(runPath, LogRunFolder.LogFileName), append: false);
                _consoleWriter = new StreamWriter(
                    Path.Combine(runPath, LogRunFolder.ConsoleFileName), append: false);
            }
            catch
            {
                _structuredWriter?.Dispose();
                _structuredWriter = null;
                LogRunFolder.EndRun(runRoot);
                throw;
            }

            _currentRunRoot = runRoot;

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
            try
            {
                FlushPendingMessages();

                if (manifest is not null && _currentRunId is not null)
                {
                    var basePath = _config.CurrentValue.LogsBasePath;
                    if (!string.IsNullOrWhiteSpace(basePath))
                    {
                        var manifestWithCount = manifest with { LogEntryCount = _logEntryCount };
                        var manifestPath = Path.Combine(basePath, _currentRunId, LogRunFolder.ManifestFileName);
                        var json = JsonSerializer.Serialize(manifestWithCount, ManifestJsonOptions);
                        File.WriteAllText(manifestPath, json);
                    }
                }
            }
            finally
            {
                // A manifest that cannot be written must not leave the run's files and drain thread open.
                CloseCurrentRun();
            }
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
        if (_currentRunRoot is not null)
            LogRunFolder.EndRun(_currentRunRoot);
        _currentRunRoot = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Safe to call twice. The container disposes this instance once for each registration that resolved it (the
    /// <c>ILoggerProvider</c> one and the concrete one that forwards to it), and a second pass over a disposed queue throws.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        CompleteRun();
        _messageQueue.Dispose();
    }

    private int _disposed;
}
