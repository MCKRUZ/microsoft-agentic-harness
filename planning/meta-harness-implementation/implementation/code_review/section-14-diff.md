diff --git a/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs
new file mode 100644
index 0000000..4cdbff8
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommand.cs
@@ -0,0 +1,58 @@
+using MediatR;
+
+namespace Application.Core.CQRS.MetaHarness;
+
+/// <summary>
+/// MediatR command that starts or resumes a full meta-harness optimization run.
+/// Validated by <see cref="RunHarnessOptimizationCommandValidator"/>.
+/// Handled by <see cref="RunHarnessOptimizationCommandHandler"/>.
+/// </summary>
+public sealed record RunHarnessOptimizationCommand : IRequest<OptimizationResult>
+{
+    /// <summary>
+    /// Identifies this optimization run. Must not be <see cref="Guid.Empty"/>.
+    /// Used to name the trace directory and group all candidate records.
+    /// </summary>
+    public required Guid OptimizationRunId { get; init; }
+
+    /// <summary>
+    /// Optional: resume from a prior candidate's snapshot rather than the currently active harness.
+    /// When null, the seed snapshot is built from the live configuration via
+    /// <see cref="Application.AI.Common.Interfaces.MetaHarness.ISnapshotBuilder"/>.
+    /// </summary>
+    public Guid? SeedCandidateId { get; init; }
+
+    /// <summary>
+    /// Optional override for
+    /// <see cref="Domain.Common.Config.MetaHarness.MetaHarnessConfig.MaxIterations"/>.
+    /// When provided, must be greater than zero.
+    /// </summary>
+    public int? MaxIterations { get; init; }
+}
+
+/// <summary>
+/// Result of a completed optimization run.
+/// </summary>
+public sealed record OptimizationResult
+{
+    /// <summary>The optimization run that produced this result.</summary>
+    public required Guid OptimizationRunId { get; init; }
+
+    /// <summary>
+    /// <see cref="Domain.Common.MetaHarness.HarnessCandidate.CandidateId"/> of the
+    /// best-scoring candidate, or null when no candidates were evaluated.
+    /// </summary>
+    public Guid? BestCandidateId { get; init; }
+
+    /// <summary>Pass rate [0.0, 1.0] of the best candidate; 0.0 when no candidates evaluated.</summary>
+    public double BestScore { get; init; }
+
+    /// <summary>Total number of iterations executed (including failure iterations).</summary>
+    public int IterationCount { get; init; }
+
+    /// <summary>
+    /// Absolute path to the <c>_proposed/</c> directory containing the best candidate's snapshot.
+    /// Empty string when no iterations completed successfully.
+    /// </summary>
+    public required string ProposedChangesPath { get; init; }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs
new file mode 100644
index 0000000..f12fd02
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandHandler.cs
@@ -0,0 +1,506 @@
+using System.Security.Cryptography;
+using System.Text;
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Application.AI.Common.Exceptions;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using MediatR;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Application.Core.CQRS.MetaHarness;
+
+/// <summary>
+/// MediatR handler for <see cref="RunHarnessOptimizationCommand"/>.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Orchestrates the full propose-evaluate iteration cycle:
+/// <list type="number">
+///   <item>Load eval tasks from <see cref="MetaHarnessConfig.EvalTasksPath"/>.</item>
+///   <item>Resolve or build the seed candidate.</item>
+///   <item>For each iteration: propose → build snapshot → evaluate → score → track best.</item>
+///   <item>Write final <c>_proposed/</c> snapshot and <c>summary.md</c>.</item>
+/// </list>
+/// </para>
+/// <para>
+/// Recoverable failures (proposer parse errors, evaluation exceptions) are caught per-iteration,
+/// recorded as <see cref="HarnessCandidateStatus.Failed"/> candidates, and do not abort the run.
+/// <see cref="OperationCanceledException"/> always propagates to the caller.
+/// </para>
+/// </remarks>
+public sealed class RunHarnessOptimizationCommandHandler
+    : IRequestHandler<RunHarnessOptimizationCommand, OptimizationResult>
+{
+    private readonly IHarnessProposer _proposer;
+    private readonly IEvaluationService _evaluationService;
+    private readonly IHarnessCandidateRepository _candidateRepository;
+    private readonly ISnapshotBuilder _snapshotBuilder;
+    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
+    private readonly ILogger<RunHarnessOptimizationCommandHandler> _logger;
+
+    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
+    {
+        WriteIndented = true,
+        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
+    };
+
+    /// <summary>Initializes a new instance of <see cref="RunHarnessOptimizationCommandHandler"/>.</summary>
+    public RunHarnessOptimizationCommandHandler(
+        IHarnessProposer proposer,
+        IEvaluationService evaluationService,
+        IHarnessCandidateRepository candidateRepository,
+        ISnapshotBuilder snapshotBuilder,
+        IOptionsMonitor<MetaHarnessConfig> config,
+        ILogger<RunHarnessOptimizationCommandHandler> logger)
+    {
+        ArgumentNullException.ThrowIfNull(proposer);
+        ArgumentNullException.ThrowIfNull(evaluationService);
+        ArgumentNullException.ThrowIfNull(candidateRepository);
+        ArgumentNullException.ThrowIfNull(snapshotBuilder);
+        ArgumentNullException.ThrowIfNull(config);
+        ArgumentNullException.ThrowIfNull(logger);
+        _proposer = proposer;
+        _evaluationService = evaluationService;
+        _candidateRepository = candidateRepository;
+        _snapshotBuilder = snapshotBuilder;
+        _config = config;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<OptimizationResult> Handle(
+        RunHarnessOptimizationCommand command,
+        CancellationToken cancellationToken)
+    {
+        var cfg = _config.CurrentValue;
+        var maxIterations = command.MaxIterations ?? cfg.MaxIterations;
+        var runDir = Path.Combine(
+            cfg.TraceDirectoryRoot, "optimizations", command.OptimizationRunId.ToString());
+        Directory.CreateDirectory(runDir);
+
+        // Abort early if no eval tasks — not a crash, just a no-op with a warning
+        var evalTasks = await LoadEvalTasksAsync(cfg.EvalTasksPath, cancellationToken);
+        if (evalTasks.Count == 0)
+        {
+            _logger.LogWarning(
+                "No eval tasks found at '{Path}'. Optimization run {RunId} completed with 0 iterations.",
+                cfg.EvalTasksPath, command.OptimizationRunId);
+            return new OptimizationResult
+            {
+                OptimizationRunId = command.OptimizationRunId,
+                BestCandidateId = null,
+                BestScore = 0.0,
+                IterationCount = 0,
+                ProposedChangesPath = string.Empty,
+            };
+        }
+
+        EnforceRetentionPolicy(cfg.MaxRunsToKeep, cfg.TraceDirectoryRoot, command.OptimizationRunId);
+
+        var manifest = LoadOrCreateRunManifest(runDir, command.OptimizationRunId);
+        var startIteration = manifest.LastCompletedIteration + 1;
+
+        var currentCandidate = await ResolveSeedCandidateAsync(command, cfg, cancellationToken);
+        HarnessCandidate? currentBestCandidate = null;
+        var priorCandidateIds = new List<Guid>();
+
+        for (var i = startIteration; i <= maxIterations; i++)
+        {
+            cancellationToken.ThrowIfCancellationRequested();
+
+            // Step 1: Propose
+            HarnessProposal proposal;
+            try
+            {
+                var proposerCtx = new HarnessProposerContext
+                {
+                    CurrentCandidate = currentCandidate,
+                    OptimizationRunDirectoryPath = runDir,
+                    PriorCandidateIds = priorCandidateIds.AsReadOnly(),
+                    Iteration = i,
+                };
+                proposal = await _proposer.ProposeAsync(proposerCtx, cancellationToken);
+            }
+            catch (HarnessProposalParsingException ex)
+            {
+                var failed = new HarnessCandidate
+                {
+                    CandidateId = Guid.NewGuid(),
+                    OptimizationRunId = command.OptimizationRunId,
+                    ParentCandidateId = currentCandidate.CandidateId,
+                    Iteration = i,
+                    CreatedAt = DateTimeOffset.UtcNow,
+                    Snapshot = currentCandidate.Snapshot,
+                    Status = HarnessCandidateStatus.Failed,
+                    FailureReason = ex.Message,
+                };
+                await _candidateRepository.SaveAsync(failed, cancellationToken);
+                priorCandidateIds.Add(failed.CandidateId);
+                _logger.LogWarning(
+                    "Iteration {Iteration}: proposer parsing failure — {Message}", i, ex.Message);
+                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
+                    command.OptimizationRunId, manifest.StartedAt);
+                continue;
+            }
+
+            // Step 2: Create candidate from proposal
+            var newSnapshot = BuildSnapshotFromProposal(currentCandidate.Snapshot, proposal);
+            var candidate = new HarnessCandidate
+            {
+                CandidateId = Guid.NewGuid(),
+                OptimizationRunId = command.OptimizationRunId,
+                ParentCandidateId = currentCandidate.CandidateId,
+                Iteration = i,
+                CreatedAt = DateTimeOffset.UtcNow,
+                Snapshot = newSnapshot,
+                Status = HarnessCandidateStatus.Proposed,
+            };
+            await _candidateRepository.SaveAsync(candidate, cancellationToken);
+            WriteSnapshotFiles(runDir, candidate);
+
+            // Step 3: Evaluate
+            EvaluationResult evalResult;
+            try
+            {
+                evalResult = await _evaluationService.EvaluateAsync(
+                    candidate, evalTasks, cancellationToken);
+            }
+            catch (Exception ex) when (ex is not OperationCanceledException)
+            {
+                var failed = candidate with
+                {
+                    Status = HarnessCandidateStatus.Failed,
+                    FailureReason = ex.Message,
+                };
+                await _candidateRepository.SaveAsync(failed, cancellationToken);
+                priorCandidateIds.Add(failed.CandidateId);
+                _logger.LogWarning(
+                    "Iteration {Iteration}: evaluation exception — {Message}", i, ex.Message);
+                UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
+                    command.OptimizationRunId, manifest.StartedAt);
+                continue;
+            }
+
+            // Step 4: Score and track best
+            var evaluated = candidate with
+            {
+                BestScore = evalResult.PassRate,
+                TokenCost = evalResult.TotalTokenCost,
+                Status = HarnessCandidateStatus.Evaluated,
+            };
+            await _candidateRepository.SaveAsync(evaluated, cancellationToken);
+            priorCandidateIds.Add(evaluated.CandidateId);
+
+            if (IsBetter(evaluated, currentBestCandidate, cfg.ScoreImprovementThreshold))
+                currentBestCandidate = evaluated;
+
+            // Step 5: Persist run state
+            UpdateRunManifest(runDir, i, currentBestCandidate?.CandidateId,
+                command.OptimizationRunId, manifest.StartedAt);
+            currentCandidate = evaluated;
+        }
+
+        var bestCandidate = await _candidateRepository.GetBestAsync(
+            command.OptimizationRunId, cancellationToken);
+        var proposedDir = Path.Combine(runDir, "_proposed");
+        WriteProposedSnapshot(proposedDir, bestCandidate);
+        await WriteSummaryMarkdownAsync(runDir, command.OptimizationRunId, cancellationToken);
+
+        return new OptimizationResult
+        {
+            OptimizationRunId = command.OptimizationRunId,
+            BestCandidateId = bestCandidate?.CandidateId,
+            BestScore = bestCandidate?.BestScore ?? 0.0,
+            IterationCount = Math.Max(0, maxIterations - startIteration + 1),
+            ProposedChangesPath = proposedDir,
+        };
+    }
+
+    private static async Task<IReadOnlyList<EvalTask>> LoadEvalTasksAsync(
+        string path, CancellationToken ct)
+    {
+        if (!Directory.Exists(path))
+            return [];
+
+        var tasks = new List<EvalTask>();
+        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
+        {
+            try
+            {
+                var json = await File.ReadAllTextAsync(file, ct);
+                var task = JsonSerializer.Deserialize<EvalTask>(json, JsonOptions);
+                if (task is not null)
+                    tasks.Add(task);
+            }
+            catch
+            {
+                // Skip malformed task files — caller logs at the run level
+            }
+        }
+        return tasks;
+    }
+
+    private static RunManifest LoadOrCreateRunManifest(string runDir, Guid optimizationRunId)
+    {
+        var path = Path.Combine(runDir, "run_manifest.json");
+        if (File.Exists(path))
+        {
+            try
+            {
+                var existing = JsonSerializer.Deserialize<RunManifest>(
+                    File.ReadAllText(path), JsonOptions);
+                if (existing is not null)
+                    return existing;
+            }
+            catch
+            {
+                // Fall through to create default
+            }
+        }
+
+        return new RunManifest
+        {
+            OptimizationRunId = optimizationRunId,
+            LastCompletedIteration = 0,
+            BestCandidateId = null,
+            StartedAt = DateTimeOffset.UtcNow,
+            WriteCompleted = false,
+        };
+    }
+
+    private static void UpdateRunManifest(
+        string runDir,
+        int lastCompletedIteration,
+        Guid? bestCandidateId,
+        Guid optimizationRunId,
+        DateTimeOffset startedAt)
+    {
+        var updated = new RunManifest
+        {
+            OptimizationRunId = optimizationRunId,
+            LastCompletedIteration = lastCompletedIteration,
+            BestCandidateId = bestCandidateId,
+            StartedAt = startedAt,
+            WriteCompleted = true,
+        };
+        var json = JsonSerializer.Serialize(updated, JsonOptions);
+        var path = Path.Combine(runDir, "run_manifest.json");
+        var tmp = path + ".tmp";
+        File.WriteAllText(tmp, json);
+        File.Move(tmp, path, overwrite: true);
+    }
+
+    /// <summary>
+    /// Returns true when <paramref name="candidate"/> should replace <paramref name="currentBest"/>.
+    /// </summary>
+    /// <remarks>
+    /// Tie-breaking order:
+    /// <list type="number">
+    ///   <item>Pass rate exceeds current best by more than <paramref name="threshold"/> → clear improvement.</item>
+    ///   <item>Pass rate within threshold (tie) → lower token cost wins.</item>
+    ///   <item>Pass rate and cost both tied → lower iteration wins (keep earlier candidate).</item>
+    /// </list>
+    /// </remarks>
+    private static bool IsBetter(
+        HarnessCandidate candidate,
+        HarnessCandidate? currentBest,
+        double threshold)
+    {
+        if (currentBest is null)
+            return true;
+
+        var delta = candidate.BestScore.GetValueOrDefault()
+                    - currentBest.BestScore.GetValueOrDefault();
+
+        if (delta > threshold)
+            return true;
+
+        if (delta < -threshold)
+            return false;
+
+        // Within threshold — tie-break by token cost
+        var costDelta = candidate.TokenCost.GetValueOrDefault()
+                        - currentBest.TokenCost.GetValueOrDefault();
+        if (costDelta < 0) return true;
+        if (costDelta > 0) return false;
+
+        // Tie on cost — prefer earlier iteration
+        return candidate.Iteration < currentBest.Iteration;
+    }
+
+    private static void WriteSnapshotFiles(string runDir, HarnessCandidate candidate)
+    {
+        var snapshotDir = Path.Combine(
+            runDir, "candidates", candidate.CandidateId.ToString(), "snapshot");
+        Directory.CreateDirectory(snapshotDir);
+
+        foreach (var (relativePath, content) in candidate.Snapshot.SkillFileSnapshots)
+        {
+            var filePath = Path.Combine(snapshotDir, relativePath);
+            var dir = Path.GetDirectoryName(filePath);
+            if (dir is not null)
+                Directory.CreateDirectory(dir);
+            File.WriteAllText(filePath, content);
+        }
+    }
+
+    private static void WriteProposedSnapshot(string proposedDir, HarnessCandidate? best)
+    {
+        if (best is null)
+            return;
+
+        if (Directory.Exists(proposedDir))
+            Directory.Delete(proposedDir, recursive: true);
+        Directory.CreateDirectory(proposedDir);
+
+        foreach (var (relativePath, content) in best.Snapshot.SkillFileSnapshots)
+        {
+            var filePath = Path.Combine(proposedDir, relativePath);
+            var dir = Path.GetDirectoryName(filePath);
+            if (dir is not null)
+                Directory.CreateDirectory(dir);
+            File.WriteAllText(filePath, content);
+        }
+    }
+
+    private async Task WriteSummaryMarkdownAsync(
+        string runDir, Guid optimizationRunId, CancellationToken ct)
+    {
+        var candidates = await _candidateRepository.ListAsync(optimizationRunId, ct);
+        var sb = new StringBuilder();
+        sb.AppendLine("# Optimization Run Summary");
+        sb.AppendLine();
+        sb.AppendLine("| Iteration | CandidateId | PassRate | TokenCost | Status |");
+        sb.AppendLine("|-----------|-------------|----------|-----------|--------|");
+        foreach (var c in candidates.OrderBy(x => x.Iteration))
+        {
+            sb.AppendLine(
+                $"| {c.Iteration} | {c.CandidateId:N} " +
+                $"| {c.BestScore?.ToString("P2") ?? "-"} " +
+                $"| {c.TokenCost?.ToString() ?? "-"} " +
+                $"| {c.Status} |");
+        }
+        await File.WriteAllTextAsync(Path.Combine(runDir, "summary.md"), sb.ToString(), ct);
+    }
+
+    private void EnforceRetentionPolicy(
+        int maxRunsToKeep, string traceDirectoryRoot, Guid currentRunId)
+    {
+        if (maxRunsToKeep <= 0)
+            return;
+
+        var optimizationsDir = Path.Combine(traceDirectoryRoot, "optimizations");
+        if (!Directory.Exists(optimizationsDir))
+            return;
+
+        var others = Directory.GetDirectories(optimizationsDir)
+            .Where(d => !string.Equals(
+                Path.GetFileName(d), currentRunId.ToString(),
+                StringComparison.OrdinalIgnoreCase))
+            .Select(d => new DirectoryInfo(d))
+            .OrderBy(d => d.CreationTimeUtc)
+            .ToList();
+
+        var excess = others.Count - (maxRunsToKeep - 1);
+        for (var i = 0; i < excess; i++)
+        {
+            try
+            {
+                Directory.Delete(others[i].FullName, recursive: true);
+                _logger.LogInformation(
+                    "Retention policy: deleted old optimization run '{Dir}'",
+                    others[i].Name);
+            }
+            catch (Exception ex)
+            {
+                _logger.LogWarning(
+                    ex, "Retention policy: failed to delete '{Dir}'", others[i].FullName);
+            }
+        }
+    }
+
+    private async Task<HarnessCandidate> ResolveSeedCandidateAsync(
+        RunHarnessOptimizationCommand command,
+        MetaHarnessConfig config,
+        CancellationToken ct)
+    {
+        if (command.SeedCandidateId.HasValue)
+        {
+            var existing = await _candidateRepository.GetAsync(command.SeedCandidateId.Value, ct);
+            if (existing is not null)
+                return existing;
+        }
+
+        var snapshot = await _snapshotBuilder.BuildAsync(
+            config.SeedCandidatePath,
+            systemPrompt: string.Empty,
+            configValues: new Dictionary<string, string>(),
+            ct);
+
+        var seed = new HarnessCandidate
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = command.OptimizationRunId,
+            ParentCandidateId = null,
+            Iteration = 0,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Snapshot = snapshot,
+            Status = HarnessCandidateStatus.Proposed,
+        };
+        await _candidateRepository.SaveAsync(seed, ct);
+        return seed;
+    }
+
+    private static HarnessSnapshot BuildSnapshotFromProposal(
+        HarnessSnapshot current, HarnessProposal proposal)
+    {
+        var skillFiles = new Dictionary<string, string>(current.SkillFileSnapshots);
+        foreach (var (path, content) in proposal.ProposedSkillChanges)
+            skillFiles[path] = content;
+
+        var configSnapshot = new Dictionary<string, string>(current.ConfigSnapshot);
+        foreach (var (key, value) in proposal.ProposedConfigChanges)
+            configSnapshot[key] = value;
+
+        var systemPrompt = proposal.ProposedSystemPromptChange ?? current.SystemPromptSnapshot;
+
+        var manifest = skillFiles
+            .Select(kvp => new SnapshotEntry(kvp.Key, ComputeSha256(kvp.Value)))
+            .ToList();
+
+        return new HarnessSnapshot
+        {
+            SkillFileSnapshots = skillFiles,
+            SystemPromptSnapshot = systemPrompt,
+            ConfigSnapshot = configSnapshot,
+            SnapshotManifest = manifest,
+        };
+    }
+
+    private static string ComputeSha256(string content)
+    {
+        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
+        return Convert.ToHexStringLower(bytes);
+    }
+
+    private sealed record RunManifest
+    {
+        [JsonPropertyName("optimizationRunId")]
+        public Guid OptimizationRunId { get; init; }
+
+        [JsonPropertyName("lastCompletedIteration")]
+        public int LastCompletedIteration { get; init; }
+
+        [JsonPropertyName("bestCandidateId")]
+        public Guid? BestCandidateId { get; init; }
+
+        [JsonPropertyName("startedAt")]
+        public DateTimeOffset StartedAt { get; init; }
+
+        [JsonPropertyName("write_completed")]
+        public bool WriteCompleted { get; init; }
+    }
+}
diff --git a/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs
new file mode 100644
index 0000000..788a031
--- /dev/null
+++ b/src/Content/Application/Application.Core/CQRS/MetaHarness/RunHarnessOptimizationCommandValidator.cs
@@ -0,0 +1,25 @@
+using FluentValidation;
+
+namespace Application.Core.CQRS.MetaHarness;
+
+/// <summary>
+/// FluentValidation validator for <see cref="RunHarnessOptimizationCommand"/>.
+/// Registered automatically via MediatR assembly scanning in
+/// <see cref="DependencyInjection.AddApplicationCoreDependencies"/>.
+/// </summary>
+public sealed class RunHarnessOptimizationCommandValidator
+    : AbstractValidator<RunHarnessOptimizationCommand>
+{
+    /// <summary>Initializes validation rules for <see cref="RunHarnessOptimizationCommand"/>.</summary>
+    public RunHarnessOptimizationCommandValidator()
+    {
+        RuleFor(x => x.OptimizationRunId)
+            .NotEqual(Guid.Empty)
+            .WithMessage("OptimizationRunId must not be empty.");
+
+        RuleFor(x => x.MaxIterations)
+            .GreaterThan(0)
+            .WithMessage("MaxIterations must be greater than zero when specified.")
+            .When(x => x.MaxIterations.HasValue);
+    }
+}
diff --git a/src/Content/Tests/Application.Core.Tests/CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs b/src/Content/Tests/Application.Core.Tests/CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs
new file mode 100644
index 0000000..2138b37
--- /dev/null
+++ b/src/Content/Tests/Application.Core.Tests/CQRS/MetaHarness/RunHarnessOptimizationCommandHandlerTests.cs
@@ -0,0 +1,664 @@
+using Application.AI.Common.Exceptions;
+using Application.AI.Common.Interfaces.MetaHarness;
+using Application.Core.CQRS.MetaHarness;
+using Domain.Common.Config.MetaHarness;
+using Domain.Common.MetaHarness;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Moq;
+using System.Text.Json;
+using Xunit;
+
+namespace Application.Core.Tests.CQRS.MetaHarness;
+
+/// <summary>
+/// Unit tests for <see cref="RunHarnessOptimizationCommandHandler"/>.
+/// All external collaborators are mocked. Filesystem I/O uses a temp directory per test.
+/// </summary>
+public sealed class RunHarnessOptimizationCommandHandlerTests : IDisposable
+{
+    private readonly string _tempDir;
+    private readonly Mock<IHarnessProposer> _proposer;
+    private readonly Mock<IEvaluationService> _evaluator;
+    private readonly Mock<IHarnessCandidateRepository> _repository;
+    private readonly Mock<ISnapshotBuilder> _snapshotBuilder;
+    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMonitor;
+    private readonly Mock<ILogger<RunHarnessOptimizationCommandHandler>> _logger;
+    private MetaHarnessConfig _cfg;
+
+    public RunHarnessOptimizationCommandHandlerTests()
+    {
+        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
+        Directory.CreateDirectory(_tempDir);
+
+        _proposer = new Mock<IHarnessProposer>();
+        _evaluator = new Mock<IEvaluationService>();
+        _repository = new Mock<IHarnessCandidateRepository>();
+        _snapshotBuilder = new Mock<ISnapshotBuilder>();
+        _configMonitor = new Mock<IOptionsMonitor<MetaHarnessConfig>>();
+        _logger = new Mock<ILogger<RunHarnessOptimizationCommandHandler>>();
+
+        _cfg = new MetaHarnessConfig
+        {
+            TraceDirectoryRoot = _tempDir,
+            MaxIterations = 3,
+            EvalTasksPath = Path.Combine(_tempDir, "eval-tasks"),
+            ScoreImprovementThreshold = 0.01,
+            MaxRunsToKeep = 0,
+        };
+        _configMonitor.Setup(x => x.CurrentValue).Returns(() => _cfg);
+    }
+
+    private RunHarnessOptimizationCommandHandler BuildHandler() =>
+        new(_proposer.Object, _evaluator.Object, _repository.Object,
+            _snapshotBuilder.Object, _configMonitor.Object, _logger.Object);
+
+    private static RunHarnessOptimizationCommand BuildCommand(
+        Guid? runId = null, int? maxIterations = null) =>
+        new()
+        {
+            OptimizationRunId = runId ?? Guid.NewGuid(),
+            MaxIterations = maxIterations,
+        };
+
+    private HarnessCandidate BuildCandidate(Guid runId, int iteration = 0,
+        HarnessCandidateStatus status = HarnessCandidateStatus.Proposed,
+        double? score = null, long? cost = null) =>
+        new()
+        {
+            CandidateId = Guid.NewGuid(),
+            OptimizationRunId = runId,
+            Iteration = iteration,
+            CreatedAt = DateTimeOffset.UtcNow,
+            Status = status,
+            BestScore = score,
+            TokenCost = cost,
+            Snapshot = BuildSnapshot(),
+        };
+
+    private static HarnessSnapshot BuildSnapshot(string? skillContent = null) =>
+        new()
+        {
+            SkillFileSnapshots = skillContent is null
+                ? new Dictionary<string, string>()
+                : new Dictionary<string, string> { ["SKILL.md"] = skillContent },
+            SystemPromptSnapshot = "system prompt",
+            ConfigSnapshot = new Dictionary<string, string>(),
+            SnapshotManifest = [],
+        };
+
+    private static HarnessProposal BuildProposal(
+        IReadOnlyDictionary<string, string>? skills = null) =>
+        new()
+        {
+            ProposedSkillChanges = skills ?? new Dictionary<string, string>(),
+            ProposedConfigChanges = new Dictionary<string, string>(),
+            ProposedSystemPromptChange = null,
+            Reasoning = "test reasoning",
+        };
+
+    private void CreateEvalTaskFile(string taskId = "task-1")
+    {
+        Directory.CreateDirectory(_cfg.EvalTasksPath);
+        var json = JsonSerializer.Serialize(new
+        {
+            TaskId = taskId,
+            Description = "desc",
+            InputPrompt = "prompt",
+            Tags = Array.Empty<string>(),
+        });
+        File.WriteAllText(Path.Combine(_cfg.EvalTasksPath, $"{taskId}.json"), json);
+    }
+
+    private void SetupSeedCandidate(Guid runId)
+    {
+        var seed = BuildCandidate(runId, iteration: 0);
+        _snapshotBuilder
+            .Setup(x => x.BuildAsync(
+                It.IsAny<string>(), It.IsAny<string>(),
+                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildSnapshot());
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+    }
+
+    private void SetupBestCandidate(Guid runId, double score = 0.8)
+    {
+        var best = BuildCandidate(runId, iteration: 1,
+            status: HarnessCandidateStatus.Evaluated, score: score, cost: 100);
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(best);
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new[] { best });
+    }
+
+    [Fact]
+    public async Task Handle_ExecutesMaxIterations_WhenAllSucceed()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert
+        Assert.Equal(3, result.IterationCount);
+        Assert.Equal(runId, result.OptimizationRunId);
+        // Proposer called exactly 3 times (one per iteration)
+        _proposer.Verify(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
+    }
+
+    [Fact]
+    public async Task Handle_ProposerParsingFailure_MarksFailedAndContinues()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId, score: 0.8);
+
+        var savedCandidates = new List<HarnessCandidate>();
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
+            .Returns(Task.CompletedTask);
+
+        var callCount = 0;
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                callCount++;
+                if (callCount == 1)
+                    throw new HarnessProposalParsingException("bad output");
+                return BuildProposal();
+            });
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert: all 3 iterations ran (failures count)
+        Assert.Equal(3, result.IterationCount);
+        // A failed candidate was saved with Failed status
+        Assert.Contains(savedCandidates, c => c.Status == HarnessCandidateStatus.Failed);
+    }
+
+    [Fact]
+    public async Task Handle_EvaluationException_MarksFailedAndContinues()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId, score: 0.8);
+
+        var savedCandidates = new List<HarnessCandidate>();
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
+            .Returns(Task.CompletedTask);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+
+        var evalCallCount = 0;
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                evalCallCount++;
+                if (evalCallCount == 1) throw new InvalidOperationException("eval blew up");
+                return new EvaluationResult(Guid.NewGuid(), 0.8, 100, []);
+            });
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert: all 3 iterations ran
+        Assert.Equal(3, result.IterationCount);
+        // A candidate with Failed status and FailureReason was saved
+        var failed = savedCandidates.FirstOrDefault(c => c.Status == HarnessCandidateStatus.Failed);
+        Assert.NotNull(failed);
+        Assert.NotNull(failed.FailureReason);
+    }
+
+    [Fact]
+    public async Task Handle_FailuresCountAsIterations_NotSkipped()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate?)null);
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync([]);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new HarnessProposalParsingException("bad"));
+
+        var handler = BuildHandler();
+
+        // Act — maxIterations = 3, all fail
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert: proposer called 3 times, not fewer
+        _proposer.Verify(
+            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
+            Times.Exactly(3));
+        Assert.Equal(3, result.IterationCount);
+    }
+
+    [Fact]
+    public async Task Handle_ScoreBelowThreshold_DoesNotUpdateBest()
+    {
+        // Arrange: threshold=0.1, iter1=0.5, iter2=0.505 (improvement < threshold)
+        _cfg.ScoreImprovementThreshold = 0.1;
+
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        var savedCandidates = new List<HarnessCandidate>();
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
+            .Returns(Task.CompletedTask);
+
+        var iter = 0;
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                iter++;
+                var score = iter == 1 ? 0.5 : 0.505;
+                return new EvaluationResult(Guid.NewGuid(), score, 100, []);
+            });
+
+        Guid? capturedBestId = null;
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                // Return the first evaluated candidate as the repository's best
+                var first = savedCandidates.FirstOrDefault(c =>
+                    c.Status == HarnessCandidateStatus.Evaluated && c.BestScore == 0.5);
+                return first;
+            });
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync([]);
+
+        var handler = BuildHandler();
+
+        // Act
+        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);
+
+        // Assert: run_manifest.json bestCandidateId points to the iter-1 candidate
+        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
+        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
+        var manifest = JsonDocument.Parse(manifestJson).RootElement;
+        var manifestBest = manifest.TryGetProperty("bestCandidateId", out var best)
+            ? best.GetString()
+            : null;
+
+        // The first evaluated candidate with score 0.5 should be the best
+        var firstEvaluated = savedCandidates.FirstOrDefault(c =>
+            c.Status == HarnessCandidateStatus.Evaluated && c.BestScore == 0.5);
+        Assert.NotNull(firstEvaluated);
+        Assert.Equal(firstEvaluated.CandidateId.ToString(), manifestBest);
+    }
+
+    [Fact]
+    public async Task Handle_TieOnPassRate_PicksLowerTokenCostCandidate()
+    {
+        // Arrange: both iterations return same pass rate, iter2 has lower token cost
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        var savedCandidates = new List<HarnessCandidate>();
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
+            .Returns(Task.CompletedTask);
+
+        var iter = 0;
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+            {
+                iter++;
+                var cost = iter == 1 ? 200L : 100L; // iter2 has lower cost
+                return new EvaluationResult(c.CandidateId, 0.8, cost, []);
+            });
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate?)null);
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync([]);
+
+        var handler = BuildHandler();
+
+        // Act
+        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);
+
+        // Assert: run_manifest bestCandidateId == iter-2 candidate (lower cost wins the tie)
+        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
+        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
+        var manifest = JsonDocument.Parse(manifestJson).RootElement;
+        var manifestBest = manifest.GetProperty("bestCandidateId").GetString();
+
+        var iter2Candidate = savedCandidates
+            .Where(c => c.Status == HarnessCandidateStatus.Evaluated && c.TokenCost == 100)
+            .LastOrDefault();
+        Assert.NotNull(iter2Candidate);
+        Assert.Equal(iter2Candidate.CandidateId.ToString(), manifestBest);
+    }
+
+    [Fact]
+    public async Task Handle_TieOnBoth_PicksEarlierIterationCandidate()
+    {
+        // Arrange: both iterations return same pass rate and same token cost
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        var savedCandidates = new List<HarnessCandidate>();
+        _repository
+            .Setup(x => x.SaveAsync(It.IsAny<HarnessCandidate>(), It.IsAny<CancellationToken>()))
+            .Callback<HarnessCandidate, CancellationToken>((c, _) => savedCandidates.Add(c))
+            .Returns(Task.CompletedTask);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate?)null);
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync([]);
+
+        var handler = BuildHandler();
+
+        // Act
+        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);
+
+        // Assert: run_manifest bestCandidateId == iter-1 candidate (earlier iteration wins)
+        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
+        var manifestJson = await File.ReadAllTextAsync(Path.Combine(runDir, "run_manifest.json"));
+        var manifest = JsonDocument.Parse(manifestJson).RootElement;
+        var manifestBest = manifest.GetProperty("bestCandidateId").GetString();
+
+        var iter1Candidate = savedCandidates
+            .Where(c => c.Status == HarnessCandidateStatus.Evaluated && c.Iteration == 1)
+            .FirstOrDefault();
+        Assert.NotNull(iter1Candidate);
+        Assert.Equal(iter1Candidate.CandidateId.ToString(), manifestBest);
+    }
+
+    [Fact]
+    public async Task Handle_ResumesFromManifest_SkipsAlreadyCompletedIterations()
+    {
+        // Arrange: pre-write a manifest indicating iteration 2 is already done
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+
+        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
+        Directory.CreateDirectory(runDir);
+        var existingManifest = new
+        {
+            optimizationRunId = runId.ToString(),
+            lastCompletedIteration = 2,
+            bestCandidateId = (string?)null,
+            startedAt = DateTimeOffset.UtcNow.ToString("O"),
+            write_completed = true,
+        };
+        await File.WriteAllTextAsync(
+            Path.Combine(runDir, "run_manifest.json"),
+            JsonSerializer.Serialize(existingManifest));
+
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId, score: 0.8);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert: proposer called only once (iteration 3 only)
+        _proposer.Verify(
+            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
+            Times.Once);
+        Assert.Equal(1, result.IterationCount);
+    }
+
+    [Fact]
+    public async Task Handle_WritesRunManifestAfterEachIteration()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId, score: 0.8);
+
+        var runDir = Path.Combine(_tempDir, "optimizations", runId.ToString());
+        var manifestUpdates = new List<int>();
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+            {
+                // Record manifest state after each evaluation (manifest is written after evaluate)
+                return new EvaluationResult(c.CandidateId, 0.8, 100, []);
+            });
+
+        var handler = BuildHandler();
+
+        // Act
+        await handler.Handle(BuildCommand(runId, maxIterations: 2), default);
+
+        // Assert: run_manifest.json exists and has lastCompletedIteration == 2
+        var manifestPath = Path.Combine(runDir, "run_manifest.json");
+        Assert.True(File.Exists(manifestPath));
+        var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)).RootElement;
+        Assert.Equal(2, json.GetProperty("lastCompletedIteration").GetInt32());
+        Assert.True(json.GetProperty("write_completed").GetBoolean());
+    }
+
+    [Fact]
+    public async Task Handle_WritesProposedChangesToOutputDir_AtEnd()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        var skillContent = "# Best SKILL.md content";
+        var bestCandidate = BuildCandidate(runId, 1, HarnessCandidateStatus.Evaluated, 0.9, 100);
+        bestCandidate = bestCandidate with
+        {
+            Snapshot = BuildSnapshot(skillContent),
+        };
+
+        _repository
+            .Setup(x => x.GetBestAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(bestCandidate);
+        _repository
+            .Setup(x => x.ListAsync(runId, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new[] { bestCandidate });
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.9, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 1), default);
+
+        // Assert: _proposed/ dir exists with the best candidate's skill file
+        var proposedDir = Path.Combine(_tempDir, "optimizations", runId.ToString(), "_proposed");
+        Assert.True(Directory.Exists(proposedDir));
+        Assert.Equal(proposedDir, result.ProposedChangesPath);
+        Assert.True(File.Exists(Path.Combine(proposedDir, "SKILL.md")));
+        Assert.Equal(skillContent, await File.ReadAllTextAsync(Path.Combine(proposedDir, "SKILL.md")));
+    }
+
+    [Fact]
+    public async Task Handle_CancellationRequested_StopsCleanlyBetweenIterations()
+    {
+        // Arrange
+        var runId = Guid.NewGuid();
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+
+        using var cts = new CancellationTokenSource();
+        var callCount = 0;
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() =>
+            {
+                callCount++;
+                if (callCount == 1) cts.Cancel(); // cancel after first proposal
+                return BuildProposal();
+            });
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act + Assert: OperationCanceledException propagates
+        await Assert.ThrowsAsync<OperationCanceledException>(
+            () => handler.Handle(BuildCommand(runId, maxIterations: 3), cts.Token));
+    }
+
+    [Fact]
+    public async Task Handle_RetentionPolicy_DeletesOldestRunsWhenExceedsMaxRunsToKeep()
+    {
+        // Arrange: create 3 old run directories, MaxRunsToKeep=2
+        _cfg.MaxRunsToKeep = 2;
+
+        var runId = Guid.NewGuid();
+        var optimizationsDir = Path.Combine(_tempDir, "optimizations");
+        Directory.CreateDirectory(optimizationsDir);
+
+        // Create 3 pre-existing runs (oldest first)
+        var oldRun1 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
+        var oldRun2 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
+        var oldRun3 = Path.Combine(optimizationsDir, Guid.NewGuid().ToString());
+        Directory.CreateDirectory(oldRun1);
+        await Task.Delay(10); // ensure distinct creation times
+        Directory.CreateDirectory(oldRun2);
+        await Task.Delay(10);
+        Directory.CreateDirectory(oldRun3);
+
+        CreateEvalTaskFile();
+        SetupSeedCandidate(runId);
+        SetupBestCandidate(runId);
+
+        _proposer
+            .Setup(x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(BuildProposal());
+        _evaluator
+            .Setup(x => x.EvaluateAsync(It.IsAny<HarnessCandidate>(), It.IsAny<IReadOnlyList<EvalTask>>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((HarnessCandidate c, IReadOnlyList<EvalTask> _, CancellationToken _) =>
+                new EvaluationResult(c.CandidateId, 0.8, 100, []));
+
+        var handler = BuildHandler();
+
+        // Act
+        await handler.Handle(BuildCommand(runId, maxIterations: 1), default);
+
+        // Assert: oldest runs deleted, newer ones kept
+        // MaxRunsToKeep=2 means at most 1 old run + current run = 2 total
+        Assert.False(Directory.Exists(oldRun1), "Oldest run should have been deleted");
+        Assert.False(Directory.Exists(oldRun2), "Second-oldest run should have been deleted");
+        Assert.True(Directory.Exists(oldRun3), "Most recent old run should be kept");
+    }
+
+    [Fact]
+    public async Task Handle_NoEvalTasks_ReturnsZeroIterations()
+    {
+        // Arrange: eval tasks directory is empty / missing
+        var runId = Guid.NewGuid();
+        // Do NOT create eval task files
+
+        var handler = BuildHandler();
+
+        // Act
+        var result = await handler.Handle(BuildCommand(runId, maxIterations: 3), default);
+
+        // Assert: returns immediately with 0 iterations, proposer never called
+        Assert.Equal(0, result.IterationCount);
+        Assert.Equal(runId, result.OptimizationRunId);
+        Assert.Null(result.BestCandidateId);
+        _proposer.Verify(
+            x => x.ProposeAsync(It.IsAny<HarnessProposerContext>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    public void Dispose()
+    {
+        try { Directory.Delete(_tempDir, recursive: true); }
+        catch { /* best-effort cleanup */ }
+    }
+}
