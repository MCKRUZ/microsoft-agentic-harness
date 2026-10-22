using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Traces;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Traces;

/// <summary>
/// Filesystem-backed implementation of <see cref="IExecutionTraceStore"/>.
/// Creates one directory per execution run under <c>MetaHarnessConfig.TraceDirectoryRoot</c>
/// and returns a scoped <see cref="ITraceWriter"/> for writing trace artifacts.
/// </summary>
/// <remarks>
/// A run nothing has written to for <c>MetaHarness:TraceRetention:RetainFor</c> is deleted by
/// <see cref="DeleteRunsNotWrittenSinceAsync"/> (#901): only the files this store writes, so anything else in the folder,
/// such as an optimization's candidate definitions, is kept.
/// </remarks>
[PersonalDataStore(
    "Execution-trace files under {MetaHarness:TraceDirectoryRoot} (production turns off by default, evaluation runs always): system prompts, model responses, tool-call JSONL, state snapshots and oversized tool results. Only the system prompt and summaries are secret-redacted. Run ids only; a run is deleted once nothing has been written to it for MetaHarness:TraceRetention (30 days by default), by a long-lived host's sweep; traces of a short-lived process go only if such a host shares its trace root.",
    PersonalDataCategories.ConversationContent | PersonalDataCategories.ToolInputsAndOutputs | PersonalDataCategories.DiagnosticText,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Configured,
    ErasureSupport.NotSupported,
    ExportSupport.NotSupported)]
public sealed class FileSystemExecutionTraceStore : IExecutionTraceStore, IStoreRetentionPeriod
{
    /// <summary>The file every run starts with, and so what marks a directory as a run.</summary>
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// The files a run's writer creates, by path from the run's own folder. Nothing else is deleted, at that place or any
    /// other: a file of the same name deeper in the run was not written by it.
    /// </summary>
    private static readonly Regex OwnFile = new(
        @"^(?:(?:traces\.jsonl|scores\.json|summary\.md)|(?:manifest\.json|scores\.json|summary\.md)\.tmp"
        + @"|turns/[0-9]+/(?:system_prompt\.md|tool_calls\.jsonl|model_response\.md|state_snapshot\.json)"
        + @"|turns/[0-9]+/tool_results/[^/]+\.json)\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
    /// <remarks>A period below the sweep's floor is refused by the sweep, so it is reported as no period at all.</remarks>
    public TimeSpan? CurrentRetentionPeriod =>
        _appConfig.CurrentValue.MetaHarness.TraceRetention is { Enabled: true } retention
        && retention.RetainFor >= RetentionPeriodConfig.MinimumRetainFor
            ? retention.RetainFor
            : null;

    /// <summary>
    /// Deletes up to <paramref name="maxRuns"/> trace runs nothing has written to since <paramref name="cutoff"/> (#901).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run is a folder this store created: at <c>executions/{id}</c> or
    /// <c>optimizations/{id}/candidates/{id}/eval/{task}/{id}</c> under the trace root, named by its execution id, with a
    /// <c>manifest.json</c> naming the same id. A folder that only holds a file called <c>manifest.json</c> is not a run,
    /// so a trace root pointed at a shared folder cannot reach another store's files.
    /// </para>
    /// <para>
    /// A run's age is its newest file, so a run still being written is never old, and one a crash left unfinished still
    /// ages out. Only this store's own files are deleted, the manifest last (see <see cref="RetentionDirectories"/>); a
    /// run that cannot be deleted is logged and found again by the next sweep, and does not stop the others.
    /// </para>
    /// </remarks>
    /// <returns>How many runs were removed.</returns>
    public Task<int> DeleteRunsNotWrittenSinceAsync(DateTimeOffset cutoff, int maxRuns, CancellationToken ct)
    {
        var root = Path.GetFullPath(_appConfig.CurrentValue.MetaHarness.TraceDirectoryRoot);
        var deleted = 0;
        foreach (var run in RunDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if (deleted >= maxRuns)
                break;

            try
            {
                if (!IsRunWrittenHere(run) || RetentionDirectories.NewestWriteUtc(run) >= cutoff.UtcDateTime)
                    continue;

                if (!RetentionDirectories.DeleteOwnFiles(run, ManifestFileName, path => OwnFile.IsMatch(path)))
                    continue;

                RetentionDirectories.RemoveEmptyParents(run, root);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete expired trace run {RunDirectory}; will retry on the next sweep.", run);
            }
        }

        return Task.FromResult(deleted);
    }

    /// <summary>The folders at the two places <see cref="TraceScope.ResolveDirectory"/> puts a run, named like one.</summary>
    private static IEnumerable<string> RunDirectories(string root)
    {
        var executions = RetentionDirectories.Subdirectories(Path.Combine(root, "executions"));
        var evaluations =
            from optimization in RetentionDirectories.Subdirectories(Path.Combine(root, "optimizations"))
            where IsRunId(optimization)
            from candidate in RetentionDirectories.Subdirectories(Path.Combine(optimization, "candidates"))
            where IsRunId(candidate)
            from task in RetentionDirectories.Subdirectories(Path.Combine(candidate, "eval"))
            from run in RetentionDirectories.Subdirectories(task)
            select run;

        return executions.Concat(evaluations).Where(IsRunId);
    }

    /// <summary>Whether the folder is named the way this store names one: a lowercase id.</summary>
    private static bool IsRunId(string directory)
    {
        var name = Path.GetFileName(directory);
        return Guid.TryParseExact(name, "D", out var id) && name == id.ToString("D");
    }

    /// <summary>Whether the folder's manifest names the folder's own id, as every manifest this store writes does.</summary>
    private static bool IsRunWrittenHere(string run)
    {
        var manifest = Path.Combine(run, ManifestFileName);
        if (!File.Exists(manifest))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("execution_run_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == Path.GetFileName(run);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ITraceWriter> StartRunAsync(TraceScope scope, RunMetadata metadata, CancellationToken ct = default)
    {
        var config = _appConfig.CurrentValue.MetaHarness;
        var dir = scope.ResolveDirectory(config.TraceDirectoryRoot);
        // Owner-only (#527): this directory (and its turns/tool_results subdirectories) holds
        // redacted-but-still-substantive tool-result payloads once ExecutionTracingEnabled is on.
        OwnerOnlyDirectoryHelper.Create(dir, _logger);

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
            OwnerOnlyDirectoryHelper.Create(turnDir, _logger);

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

                    OwnerOnlyDirectoryHelper.Create(toolResultsDir, _logger);
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
