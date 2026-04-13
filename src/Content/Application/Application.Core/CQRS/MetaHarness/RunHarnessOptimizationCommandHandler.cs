using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.MetaHarness;

/// <summary>
/// MediatR handler for <see cref="RunHarnessOptimizationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Orchestrates the full propose-evaluate iteration cycle:
/// <list type="number">
///   <item>Load eval tasks from <see cref="MetaHarnessConfig.EvalTasksPath"/>.</item>
///   <item>Resolve or build the seed candidate.</item>
///   <item>For each iteration: propose → build snapshot → evaluate → score → track best.</item>
///   <item>Write final <c>_proposed/</c> snapshot and <c>summary.md</c>.</item>
/// </list>
/// </para>
/// <para>
/// Recoverable failures (proposer parse errors, evaluation exceptions) are caught per-iteration,
/// recorded as <see cref="HarnessCandidateStatus.Failed"/> candidates, and do not abort the run.
/// <see cref="OperationCanceledException"/> always propagates to the caller.
/// </para>
/// </remarks>
public sealed class RunHarnessOptimizationCommandHandler
    : IRequestHandler<RunHarnessOptimizationCommand, OptimizationResult>
{
    private readonly IHarnessProposer _proposer;
    private readonly IEvaluationService _evaluationService;
    private readonly IHarnessCandidateRepository _candidateRepository;
    private readonly ISnapshotBuilder _snapshotBuilder;
    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<RunHarnessOptimizationCommandHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Initializes a new instance of <see cref="RunHarnessOptimizationCommandHandler"/>.</summary>
    public RunHarnessOptimizationCommandHandler(
        IHarnessProposer proposer,
        IEvaluationService evaluationService,
        IHarnessCandidateRepository candidateRepository,
        ISnapshotBuilder snapshotBuilder,
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<RunHarnessOptimizationCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(evaluationService);
        ArgumentNullException.ThrowIfNull(candidateRepository);
        ArgumentNullException.ThrowIfNull(snapshotBuilder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _proposer = proposer;
        _evaluationService = evaluationService;
        _candidateRepository = candidateRepository;
        _snapshotBuilder = snapshotBuilder;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OptimizationResult> Handle(
        RunHarnessOptimizationCommand command,
        CancellationToken cancellationToken)
    {
        var cfg = _config.CurrentValue;
        var maxIterations = command.MaxIterations ?? cfg.MaxIterations;
        var runDir = Path.Combine(
            cfg.TraceDirectoryRoot, "optimizations", command.OptimizationRunId.ToString());
        Directory.CreateDirectory(runDir);

        // Abort early if no eval tasks — not a crash, just a no-op with a warning
        var evalTasks = await LoadEvalTasksAsync(cfg.EvalTasksPath, cancellationToken);
        if (evalTasks.Count == 0)
        {
            _logger.LogWarning(
                "No eval tasks found at '{Path}'. Optimization run {RunId} completed with 0 iterations.",
                cfg.EvalTasksPath, command.OptimizationRunId);
            return new OptimizationResult
            {
                OptimizationRunId = command.OptimizationRunId,
                BestCandidateId = null,
                BestScore = 0.0,
                IterationCount = 0,
                ProposedChangesPath = string.Empty,
            };
        }

        EnforceRetentionPolicy(cfg.MaxRunsToKeep, cfg.TraceDirectoryRoot, command.OptimizationRunId);

        var manifest = await LoadOrCreateRunManifest(runDir, command.OptimizationRunId);
        var startIteration = manifest.LastCompletedIteration + 1;

        var currentCandidate = await ResolveSeedCandidateAsync(command, cfg, cancellationToken);
        HarnessCandidate? currentBestCandidate = null;
        var priorCandidateIds = new List<Guid>();
        var executedIterations = 0;

        for (var i = startIteration; i <= maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            executedIterations++;

            // Step 1: Propose
            HarnessProposal proposal;
            try
            {
                var proposerCtx = new HarnessProposerContext
                {
                    CurrentCandidate = currentCandidate,
                    OptimizationRunDirectoryPath = runDir,
                    PriorCandidateIds = priorCandidateIds.AsReadOnly(),
                    Iteration = i,
                };
                proposal = await _proposer.ProposeAsync(proposerCtx, cancellationToken);
            }
            catch (HarnessProposalParsingException ex)
            {
                var failed = new HarnessCandidate
                {
                    CandidateId = Guid.NewGuid(),
                    OptimizationRunId = command.OptimizationRunId,
                    ParentCandidateId = currentCandidate.CandidateId,
                    Iteration = i,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Snapshot = currentCandidate.Snapshot,
                    Status = HarnessCandidateStatus.Failed,
                    FailureReason = ex.Message,
                };
                await _candidateRepository.SaveAsync(failed, cancellationToken);
                priorCandidateIds.Add(failed.CandidateId);
                _logger.LogWarning(
                    "Iteration {Iteration}: proposer parsing failure — {Message}", i, ex.Message);
                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                    command.OptimizationRunId, manifest.StartedAt);
                continue;
            }

            // Step 2: Create candidate from proposal
            var newSnapshot = BuildSnapshotFromProposal(currentCandidate.Snapshot, proposal);
            var candidate = new HarnessCandidate
            {
                CandidateId = Guid.NewGuid(),
                OptimizationRunId = command.OptimizationRunId,
                ParentCandidateId = currentCandidate.CandidateId,
                Iteration = i,
                CreatedAt = DateTimeOffset.UtcNow,
                Snapshot = newSnapshot,
                Status = HarnessCandidateStatus.Proposed,
            };
            await _candidateRepository.SaveAsync(candidate, cancellationToken);
            WriteSnapshotFiles(runDir, candidate);

            // Step 3: Evaluate
            EvaluationResult evalResult;
            try
            {
                evalResult = await _evaluationService.EvaluateAsync(
                    candidate, evalTasks, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = candidate with
                {
                    Status = HarnessCandidateStatus.Failed,
                    FailureReason = ex.Message,
                };
                await _candidateRepository.SaveAsync(failed, cancellationToken);
                priorCandidateIds.Add(failed.CandidateId);
                _logger.LogWarning(
                    "Iteration {Iteration}: evaluation exception — {Message}", i, ex.Message);
                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                    command.OptimizationRunId, manifest.StartedAt);
                continue;
            }

            // Step 4: Score and track best
            var evaluated = candidate with
            {
                BestScore = evalResult.PassRate,
                TokenCost = evalResult.TotalTokenCost,
                Status = HarnessCandidateStatus.Evaluated,
            };
            await _candidateRepository.SaveAsync(evaluated, cancellationToken);
            priorCandidateIds.Add(evaluated.CandidateId);

            if (IsBetter(evaluated, currentBestCandidate, cfg.ScoreImprovementThreshold))
                currentBestCandidate = evaluated;

            // Step 5: Persist run state
            UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
                command.OptimizationRunId, manifest.StartedAt);
            currentCandidate = evaluated;
        }

        var bestCandidate = await _candidateRepository.GetBestAsync(
            command.OptimizationRunId, cancellationToken);
        var proposedDir = Path.Combine(runDir, "_proposed");
        WriteProposedSnapshot(proposedDir, bestCandidate);
        await WriteSummaryMarkdownAsync(runDir, command.OptimizationRunId, cancellationToken);

        return new OptimizationResult
        {
            OptimizationRunId = command.OptimizationRunId,
            BestCandidateId = bestCandidate?.CandidateId,
            BestScore = bestCandidate?.BestScore ?? 0.0,
            IterationCount = executedIterations,
            ProposedChangesPath = bestCandidate is not null ? proposedDir : string.Empty,
        };
    }

    private static async Task<IReadOnlyList<EvalTask>> LoadEvalTasksAsync(
        string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return [];

        var tasks = new List<EvalTask>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var task = JsonSerializer.Deserialize<EvalTask>(json, JsonOptions);
                if (task is not null)
                    tasks.Add(task);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Skip individual malformed/unreadable task files; directory-level errors propagate
            }
        }
        return tasks;
    }

    private static async Task<RunManifest> LoadOrCreateRunManifest(string runDir, Guid optimizationRunId)
    {
        var path = Path.Combine(runDir, "run_manifest.json");
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path);
                var existing = JsonSerializer.Deserialize<RunManifest>(json, JsonOptions);
                if (existing is not null)
                    return existing;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Corrupt manifest — recreate from scratch
            }
        }

        return new RunManifest
        {
            OptimizationRunId = optimizationRunId,
            LastCompletedIteration = 0,
            BestCandidateId = null,
            StartedAt = DateTimeOffset.UtcNow,
            WriteCompleted = false,
        };
    }

    private static void UpdateRunManifest(
        string runDir,
        int lastCompletedIteration,
        Guid? bestCandidateId,
        Guid optimizationRunId,
        DateTimeOffset startedAt)
    {
        var updated = new RunManifest
        {
            OptimizationRunId = optimizationRunId,
            LastCompletedIteration = lastCompletedIteration,
            BestCandidateId = bestCandidateId,
            StartedAt = startedAt,
            WriteCompleted = true,
        };
        var json = JsonSerializer.Serialize(updated, JsonOptions);
        var path = Path.Combine(runDir, "run_manifest.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> should replace <paramref name="currentBest"/>.
    /// </summary>
    /// <remarks>
    /// Tie-breaking order:
    /// <list type="number">
    ///   <item>Pass rate exceeds current best by more than <paramref name="threshold"/> → clear improvement.</item>
    ///   <item>Pass rate within threshold (tie) → lower token cost wins.</item>
    ///   <item>Pass rate and cost both tied → lower iteration wins (keep earlier candidate).</item>
    /// </list>
    /// </remarks>
    private static bool IsBetter(
        HarnessCandidate candidate,
        HarnessCandidate? currentBest,
        double threshold)
    {
        if (currentBest is null)
            return true;

        var delta = candidate.BestScore.GetValueOrDefault()
                    - currentBest.BestScore.GetValueOrDefault();

        if (delta > threshold)
            return true;

        if (delta < -threshold)
            return false;

        // Within threshold — tie-break by token cost
        var costDelta = candidate.TokenCost.GetValueOrDefault()
                        - currentBest.TokenCost.GetValueOrDefault();
        if (costDelta < 0) return true;
        if (costDelta > 0) return false;

        // Tie on cost — prefer earlier iteration
        return candidate.Iteration < currentBest.Iteration;
    }

    private static void WriteSnapshotFiles(string runDir, HarnessCandidate candidate)
    {
        var snapshotDir = Path.Combine(
            runDir, "candidates", candidate.CandidateId.ToString(), "snapshot");
        Directory.CreateDirectory(snapshotDir);

        foreach (var (relativePath, content) in candidate.Snapshot.SkillFileSnapshots)
        {
            var filePath = SafeResolvePath(snapshotDir, relativePath);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content);
        }
    }

    private static void WriteProposedSnapshot(string proposedDir, HarnessCandidate? best)
    {
        if (best is null)
            return;

        if (Directory.Exists(proposedDir))
            Directory.Delete(proposedDir, recursive: true);
        Directory.CreateDirectory(proposedDir);

        foreach (var (relativePath, content) in best.Snapshot.SkillFileSnapshots)
        {
            var filePath = SafeResolvePath(proposedDir, relativePath);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, content);
        }
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="baseDir"/> and asserts
    /// the result stays within <paramref name="baseDir"/>. Throws on path traversal attempts.
    /// </summary>
    private static string SafeResolvePath(string baseDir, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        var rootWithSep = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Proposed skill path '{relativePath}' resolves outside the snapshot directory.");
        }
        return resolved;
    }

    private async Task WriteSummaryMarkdownAsync(
        string runDir, Guid optimizationRunId, CancellationToken ct)
    {
        var candidates = await _candidateRepository.ListAsync(optimizationRunId, ct);
        var sb = new StringBuilder();
        sb.AppendLine("# Optimization Run Summary");
        sb.AppendLine();
        sb.AppendLine("| Iteration | CandidateId | PassRate | TokenCost | Status |");
        sb.AppendLine("|-----------|-------------|----------|-----------|--------|");
        foreach (var c in candidates.OrderBy(x => x.Iteration))
        {
            sb.AppendLine(
                $"| {c.Iteration} | {c.CandidateId:N} " +
                $"| {c.BestScore?.ToString("P2") ?? "-"} " +
                $"| {c.TokenCost?.ToString() ?? "-"} " +
                $"| {c.Status} |");
        }
        await File.WriteAllTextAsync(Path.Combine(runDir, "summary.md"), sb.ToString(), ct);
    }

    private void EnforceRetentionPolicy(
        int maxRunsToKeep, string traceDirectoryRoot, Guid currentRunId)
    {
        if (maxRunsToKeep <= 0)
            return;

        var optimizationsDir = Path.Combine(traceDirectoryRoot, "optimizations");
        if (!Directory.Exists(optimizationsDir))
            return;

        var others = Directory.GetDirectories(optimizationsDir)
            .Where(d => Guid.TryParse(Path.GetFileName(d), out _)
                        && !string.Equals(
                            Path.GetFileName(d), currentRunId.ToString(),
                            StringComparison.OrdinalIgnoreCase))
            .Select(d => new DirectoryInfo(d))
            .OrderBy(d => d.CreationTimeUtc)
            .ToList();

        var excess = others.Count - (maxRunsToKeep - 1);
        for (var i = 0; i < excess; i++)
        {
            try
            {
                Directory.Delete(others[i].FullName, recursive: true);
                _logger.LogInformation(
                    "Retention policy: deleted old optimization run '{Dir}'",
                    others[i].Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Retention policy: failed to delete '{Dir}'", others[i].FullName);
            }
        }
    }

    private async Task<HarnessCandidate> ResolveSeedCandidateAsync(
        RunHarnessOptimizationCommand command,
        MetaHarnessConfig config,
        CancellationToken ct)
    {
        if (command.SeedCandidateId.HasValue)
        {
            var existing = await _candidateRepository.GetAsync(command.SeedCandidateId.Value, ct);
            if (existing is null)
                throw new InvalidOperationException(
                    $"Seed candidate '{command.SeedCandidateId.Value}' not found in repository.");
            return existing;
        }

        var snapshot = await _snapshotBuilder.BuildAsync(
            config.SeedCandidatePath,
            systemPrompt: string.Empty,
            configValues: new Dictionary<string, string>(),
            ct);

        var seed = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = command.OptimizationRunId,
            ParentCandidateId = null,
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = snapshot,
            Status = HarnessCandidateStatus.Proposed,
        };
        await _candidateRepository.SaveAsync(seed, ct);
        return seed;
    }

    private static HarnessSnapshot BuildSnapshotFromProposal(
        HarnessSnapshot current, HarnessProposal proposal)
    {
        var skillFiles = new Dictionary<string, string>(current.SkillFileSnapshots);
        foreach (var (path, content) in proposal.ProposedSkillChanges)
            skillFiles[path] = content;

        var configSnapshot = new Dictionary<string, string>(current.ConfigSnapshot);
        foreach (var (key, value) in proposal.ProposedConfigChanges)
            configSnapshot[key] = value;

        var systemPrompt = proposal.ProposedSystemPromptChange ?? current.SystemPromptSnapshot;

        var manifest = skillFiles
            .Select(kvp => new SnapshotEntry(kvp.Key, ComputeSha256(kvp.Value)))
            .ToList();

        return new HarnessSnapshot
        {
            SkillFileSnapshots = skillFiles,
            SystemPromptSnapshot = systemPrompt,
            ConfigSnapshot = configSnapshot,
            SnapshotManifest = manifest,
        };
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private sealed record RunManifest
    {
        [JsonPropertyName("optimizationRunId")]
        public Guid OptimizationRunId { get; init; }

        [JsonPropertyName("lastCompletedIteration")]
        public int LastCompletedIteration { get; init; }

        [JsonPropertyName("bestCandidateId")]
        public Guid? BestCandidateId { get; init; }

        [JsonPropertyName("startedAt")]
        public DateTimeOffset StartedAt { get; init; }

        [JsonPropertyName("writeCompleted")]
        public bool WriteCompleted { get; init; }
    }
}
