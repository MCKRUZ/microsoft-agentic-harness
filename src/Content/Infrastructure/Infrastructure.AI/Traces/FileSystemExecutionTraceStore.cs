using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Traces;

/// <summary>
/// Filesystem-backed implementation of <see cref="IExecutionTraceStore"/>.
/// Creates one directory per execution run under <c>MetaHarnessConfig.TraceDirectoryRoot</c>
/// and returns a scoped <see cref="ITraceWriter"/> for writing trace artifacts.
/// </summary>
public sealed class FileSystemExecutionTraceStore : IExecutionTraceStore
{
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ISecretRedactor _redactor;
    private readonly ILogger<FileSystemExecutionTraceStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemExecutionTraceStore"/>.
    /// </summary>
    public FileSystemExecutionTraceStore(
        IOptionsMonitor<AppConfig> appConfig,
        ISecretRedactor redactor,
        ILogger<FileSystemExecutionTraceStore> logger)
    {
        _appConfig = appConfig;
        _redactor = redactor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ITraceWriter> StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken ct = default)
    {
        var config = _appConfig.CurrentValue.MetaHarness;
        var dir = scope.ResolveDirectory(config.TraceDirectoryRoot);
        Directory.CreateDirectory(dir);

        var manifest = new
        {
            execution_run_id = scope.ExecutionRunId.ToString("D"),
            agent_name = metadata.AgentName,
            started_at = metadata.StartedAt.ToString("O"),
            write_completed = false
        };
        await WriteAtomicAsync(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest));

        _logger.LogDebug(
            "Started trace run {RunId} in {Dir}",
            scope.ExecutionRunId, dir);

        return new FileSystemTraceWriter(dir, scope, _redactor, config, _logger);
    }

    /// <inheritdoc />
    public Task<string> GetRunDirectoryAsync(TraceScope scope, CancellationToken ct = default)
    {
        var root = _appConfig.CurrentValue.MetaHarness.TraceDirectoryRoot;
        return Task.FromResult(scope.ResolveDirectory(root));
    }

    private static async Task WriteAtomicAsync(string targetPath, string content)
    {
        var tmp = targetPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, targetPath, overwrite: true);
    }

    // -------------------------------------------------------------------------
    // FileSystemTraceWriter — scoped writer for one execution run
    // -------------------------------------------------------------------------

    private sealed class FileSystemTraceWriter : ITraceWriter
    {
        private readonly ISecretRedactor _redactor;
        private readonly MetaHarnessConfig _config;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _tracesLock = new(1, 1);
        private long _sequenceCounter;

        public TraceScope Scope { get; }
        public string RunDirectory { get; }

        public FileSystemTraceWriter(
            string runDirectory,
            TraceScope scope,
            ISecretRedactor redactor,
            MetaHarnessConfig config,
            ILogger logger)
        {
            RunDirectory = runDirectory;
            Scope = scope;
            _redactor = redactor;
            _config = config;
            _logger = logger;
        }

        public async Task WriteTurnAsync(int turnNumber, TurnArtifacts artifacts, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turnNumber);

            var turnDir = Path.Combine(RunDirectory, "turns", turnNumber.ToString());
            Directory.CreateDirectory(turnDir);

            if (artifacts.SystemPrompt is { } prompt)
            {
                var redacted = _redactor.Redact(prompt) ?? string.Empty;
                await File.WriteAllTextAsync(Path.Combine(turnDir, "system_prompt.md"), redacted, ct);
            }

            if (artifacts.ToolCallsJsonl is { } calls)
                await File.WriteAllTextAsync(Path.Combine(turnDir, "tool_calls.jsonl"), calls, ct);

            if (artifacts.ModelResponse is { } response)
                await File.WriteAllTextAsync(Path.Combine(turnDir, "model_response.md"), response, ct);

            if (artifacts.StateSnapshot is { } snapshot)
                await File.WriteAllTextAsync(Path.Combine(turnDir, "state_snapshot.json"), snapshot, ct);

            if (artifacts.ToolResults.Count > 0)
            {
                var maxBytes = _config.MaxFullPayloadKB * 1024;
                var toolResultsDir = Path.GetFullPath(Path.Combine(turnDir, "tool_results"));

                foreach (var (callId, result) in artifacts.ToolResults)
                {
                    if (Encoding.UTF8.GetByteCount(result) <= maxBytes)
                        continue;

                    // Guard against path traversal: callId comes from the LLM / framework
                    var safeCallId = SanitizeFileName(callId);
                    var targetPath = Path.GetFullPath(Path.Combine(toolResultsDir, $"{safeCallId}.json"));
                    if (!targetPath.StartsWith(toolResultsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Skipping tool result with suspicious CallId {CallId} — resolved outside tool_results dir",
                            callId);
                        continue;
                    }

                    Directory.CreateDirectory(toolResultsDir);
                    await File.WriteAllTextAsync(targetPath, result, ct);
                }
            }
        }

        public async Task AppendTraceAsync(ExecutionTraceRecord record, CancellationToken ct = default)
        {
            var seq = Interlocked.Increment(ref _sequenceCounter);

            var redactedSummary = _redactor.Redact(record.PayloadSummary);
            if (redactedSummary?.Length > 500)
                redactedSummary = redactedSummary[..500];

            var finalRecord = record with
            {
                Seq = seq,
                Ts = record.Ts == default ? DateTimeOffset.UtcNow : record.Ts,
                PayloadSummary = redactedSummary
            };

            var line = JsonSerializer.Serialize(finalRecord, JsonOptions) + "\n";

            await _tracesLock.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(
                    Path.Combine(RunDirectory, "traces.jsonl"), line, ct);
            }
            finally
            {
                _tracesLock.Release();
            }
        }

        public async Task WriteScoresAsync(HarnessScores scores, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(scores, JsonOptions);
            await WriteAtomicAsync(Path.Combine(RunDirectory, "scores.json"), json);
        }

        public async Task WriteSummaryAsync(string markdown, CancellationToken ct = default)
        {
            await WriteAtomicAsync(Path.Combine(RunDirectory, "summary.md"), markdown);
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            var manifestPath = Path.Combine(RunDirectory, "manifest.json");
            var existing = await File.ReadAllTextAsync(manifestPath, ct);

            // Parse existing manifest and update write_completed flag
            using var doc = JsonDocument.Parse(existing);
            var props = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                props[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)prop.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    _ => prop.Value.GetRawText()
                };
            }
            props["write_completed"] = true;

            await WriteAtomicAsync(manifestPath, JsonSerializer.Serialize(props, JsonOptions));
        }

        public ValueTask DisposeAsync()
        {
            _tracesLock.Dispose();
            return ValueTask.CompletedTask;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }
    }
}
