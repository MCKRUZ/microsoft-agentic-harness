using System.Text.RegularExpressions;
using Application.AI.Common.Extensions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Traces;
using Application.AI.Common.Services.Governance;
using Domain.AI.Agents;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Evaluates a harness candidate by running each eval task against the candidate's
/// in-memory skill snapshot, grading outputs via regex, and writing per-task traces.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> — each evaluation creates its own <see cref="SemaphoreSlim"/>
/// scoped to the current optimization loop iteration.
/// </remarks>
public sealed class AgentEvaluationService : IEvaluationService
{
    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly IExecutionTraceStore _traceStore;
    private readonly IAgentFactory _agentFactory;
    private readonly IToolCallAdmissionPipeline _admissionPipeline;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentEvaluationService> _logger;

    // Candidate-proposed scripts are never executed during evaluation — running untrusted
    // LLM-authored scripts would be an RCE vector. Mirrors AgentExecutionContextFactory.
    private static readonly AgentFileSkillScriptRunner NoOpScriptRunner =
        (skill, script, arguments, serviceProvider, cancellationToken) =>
            Task.FromResult<object?>(null);

    /// <param name="admissionPipeline">
    /// The scoped admission chain armed around every eval run's <c>agent.RunAsync</c> call (#482) —
    /// without it, <c>GovernedAIFunction</c>'s null-ambient early-return path leaves any tool a
    /// candidate-loaded skill declares completely ungoverned, the same gap #478 closed for
    /// <c>Presentation.FoundryHost</c>.
    /// </param>
    /// <param name="sanitizer">
    /// Passed to the <see cref="Application.AI.Common.Services.Agent.GoverningToolContextProvider"/> this service now wires onto its own
    /// context (#482), matching how <c>AgentExecutionContextFactory</c> wires it for every production
    /// agent — otherwise eval's skill-disclosure tools (<c>load_skill</c>/<c>read_skill_resource</c>)
    /// carry no sanitize coverage at all.
    /// </param>
    public AgentEvaluationService(
        IOptionsMonitor<MetaHarnessConfig> config,
        IExecutionTraceStore traceStore,
        IAgentFactory agentFactory,
        IToolCallAdmissionPipeline admissionPipeline,
        ICompositeResponseSanitizer sanitizer,
        ILoggerFactory loggerFactory,
        ILogger<AgentEvaluationService> logger)
    {
        _config = config;
        _traceStore = traceStore;
        _agentFactory = agentFactory;
        _admissionPipeline = admissionPipeline;
        _sanitizer = sanitizer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EvaluationResult> EvaluateAsync(
        HarnessCandidate candidate,
        IReadOnlyList<EvalTask> evalTasks,
        CancellationToken cancellationToken = default)
    {
        var cfg = _config.CurrentValue;
        var parallelism = Math.Max(1, cfg.MaxEvalParallelism);
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);

        var taskResults = await Task.WhenAll(
            evalTasks.Select(task => RunSingleTaskAsync(candidate, task, cfg, semaphore, cancellationToken)));

        var passed = taskResults.Count(r => r.Passed);
        var passRate = evalTasks.Count > 0 ? (double)passed / evalTasks.Count : 0.0;
        var totalTokenCost = taskResults.Sum(r => r.TokenCost);

        _logger.LogInformation(
            "Candidate {CandidateId}: {Passed}/{Total} tasks passed (PassRate={PassRate:F2})",
            candidate.CandidateId, passed, evalTasks.Count, passRate);

        return new EvaluationResult(candidate.CandidateId, passRate, totalTokenCost, taskResults);
    }

    private async Task<TaskEvaluationResult> RunSingleTaskAsync(
        HarnessCandidate candidate,
        EvalTask task,
        MetaHarnessConfig cfg,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteTaskAsync(candidate, task, cfg, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<TaskEvaluationResult> ExecuteTaskAsync(
        HarnessCandidate candidate,
        EvalTask task,
        MetaHarnessConfig cfg,
        CancellationToken cancellationToken)
    {
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = candidate.OptimizationRunId,
            CandidateId = candidate.CandidateId,
            TaskId = task.TaskId
        };

        var metadata = new RunMetadata
        {
            AgentName = "EvaluationAgent",
            StartedAt = DateTimeOffset.UtcNow
        };

        ITraceWriter? traceWriter = null;
        var traceCompleted = false;
        TaskEvaluationResult? taskResult = null;
        string? skillDirectory = null;

        try
        {
            traceWriter = await _traceStore.StartRunAsync(scope, metadata, cancellationToken);

            // Materialize the candidate's proposed skills so the eval agent loads them through the
            // same MAF progressive-disclosure path used in production. Without this, a candidate that
            // changes only skill files would evaluate identically to its parent.
            skillDirectory = MaterializeCandidateSkills(candidate.Snapshot, scope.ExecutionRunId);

            var context = new AgentExecutionContext
            {
                Name = "EvaluationAgent",
                Instruction = candidate.Snapshot.SystemPromptSnapshot,
                DeploymentName = string.IsNullOrEmpty(cfg.EvaluationModelVersion) ? null : cfg.EvaluationModelVersion,
                TraceScope = scope,
                AIContextProviders = BuildContextProviders(skillDirectory),
                AdditionalProperties = new Dictionary<string, object>
                {
                    [ITraceWriter.AdditionalPropertiesKey] = traceWriter
                }
            };

            var agent = await _agentFactory.CreateAgentAsync(context, cancellationToken);

            // #482: arm the same ambient admission chain ExecuteAgentTurnCommandHandler arms for every
            // production turn. Begin (not assign-and-null) so a nested/enclosing governed flow is
            // restored rather than disarmed on the way out — see ToolAdmissionAccessor's remarks.
            AgentResponse response;
            using (ToolAdmissionAccessor.Begin(_admissionPipeline))
            {
                response = await agent.RunAsync(
                    [new ChatMessage(ChatRole.User, task.InputPrompt)],
                    cancellationToken: cancellationToken);
            }

            var output = ExtractContent(response);
            var (passed, failureReason) = Grade(output, task.ExpectedOutputPattern);
            taskResult = new TaskEvaluationResult(
                task.TaskId, passed, ResolveTokenCost(response, task.InputPrompt, output), failureReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Task {TaskId} failed for candidate {CandidateId}",
                task.TaskId, candidate.CandidateId);

            // Zero here is a genuine unknown, not a placeholder: without a response there is no usage to
            // read, and the throw may have come before anything reached the provider or after a full turn
            // was billed. Estimating would put an invented number into a candidate-selection input.
            taskResult = new TaskEvaluationResult(task.TaskId, Passed: false, TokenCost: 0L, ex.Message);
        }
        finally
        {
            // Complete exactly once, then dispose. Use CancellationToken.None so the manifest
            // is finalized even when the parent cancellation token is already signalled.
            if (traceWriter is not null && !traceCompleted)
            {
                try
                {
                    await traceWriter.CompleteAsync(CancellationToken.None);
                    traceCompleted = true;
                }
                catch (Exception completionEx)
                {
                    _logger.LogWarning(completionEx, "Failed to complete trace for task {TaskId}", task.TaskId);
                }

                await traceWriter.DisposeAsync();
            }

            if (skillDirectory is not null)
                TryDeleteDirectory(skillDirectory);
        }

        return taskResult!;
    }

    /// <summary>
    /// Materializes the candidate's skill snapshot to an isolated temp directory so the eval
    /// agent can load the proposed skills via MAF's <see cref="AgentSkillsProvider"/>. Returns
    /// <see langword="null"/> when the candidate has no skill files.
    /// </summary>
    /// <remarks>
    /// Snapshot keys originate from LLM-authored proposals and are therefore untrusted: each path
    /// is resolved and asserted to stay within the temp root to block path-traversal escapes.
    /// Unchanged files are secret-redacted in the snapshot, but that redaction is constant across a
    /// candidate and its parent, so the comparative pass-rate signal is preserved; the proposed
    /// (changed) files are unredacted and faithfully evaluated.
    /// </remarks>
    private string? MaterializeCandidateSkills(HarnessSnapshot snapshot, Guid executionRunId)
    {
        if (snapshot.SkillFileSnapshots.Count == 0)
            return null;

        // Canonicalize once so the containment check compares like-for-like (handles symlinked
        // temp roots on macOS and 8.3 short names on Windows).
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "harness-eval-skills", executionRunId.ToString("N")));
        Directory.CreateDirectory(root);

        try
        {
            foreach (var (relativePath, content) in snapshot.SkillFileSnapshots)
            {
                var filePath = SafeResolveWithinRoot(root, relativePath);
                var directory = Path.GetDirectoryName(filePath);
                if (directory is not null)
                    Directory.CreateDirectory(directory);
                File.WriteAllText(filePath, content);
            }
        }
        catch
        {
            // A path-traversal rejection (or any write failure) must not leak a partial temp dir,
            // since the caller never receives the path to clean up.
            TryDeleteDirectory(root);
            throw;
        }

        return root;
    }

    /// <summary>
    /// Builds the eval context's <see cref="AIContextProvider"/> rail: the progressive-disclosure skills
    /// provider over <paramref name="skillDirectory"/> (when present) followed unconditionally by
    /// <see cref="Application.AI.Common.Services.Agent.GoverningToolContextProvider"/>, mirroring the production wiring in
    /// <c>AgentExecutionContextFactory.BuildMergedAIContextProviders</c>.
    /// </summary>
    /// <remarks>
    /// #482: the governance wrapper is attached unconditionally, same as the production factory —
    /// without it, <c>load_skill</c>/<c>read_skill_resource</c> carry no sanitize coverage on this path
    /// at all, since <c>ToolChainBuilder</c> (the other place governance gets wired in) is never
    /// consulted for an eval context built directly from a materialized skill snapshot.
    /// </remarks>
    private IList<AIContextProvider> BuildContextProviders(string? skillDirectory)
    {
        var providers = new List<AIContextProvider>();

        if (skillDirectory is not null)
        {
            providers.Add(new AgentSkillsProviderBuilder()
                .UseFileScriptRunner(NoOpScriptRunner)
                .UseOptions(SkillDisclosureDefaults.Configure)
                .UseFileSkill(skillDirectory)
                .Build());
        }

        providers.Add(new Application.AI.Common.Services.Agent.GoverningToolContextProvider(
            _loggerFactory.CreateLogger<Application.AI.Common.Services.Agent.GoverningToolContextProvider>(), _sanitizer));

        return providers;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under the canonical <paramref name="root"/> and asserts
    /// the result stays within it. Throws on path-traversal attempts in untrusted snapshot keys.
    /// </summary>
    /// <remarks>
    /// Containment is checked via <see cref="Path.GetRelativePath(string, string)"/>, which honors the
    /// host platform's path-case rules (case-insensitive on Windows, case-sensitive on Linux) — unlike a
    /// hard-coded ordinal/ignore-case string prefix check, which is wrong on at least one platform.
    /// </remarks>
    private static string SafeResolveWithinRoot(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, resolved);

        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"Candidate skill path '{relativePath}' resolves outside the eval skill directory.");
        }
        return resolved;
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up eval skill directory {Directory}", directory);
        }
    }

    private static (bool Passed, string? FailureReason) Grade(string output, string? pattern)
    {
        if (pattern is null)
            return (true, null);

        try
        {
            var match = Regex.Match(output, pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
            return match.Success ? (true, null) : (false, "pattern_not_matched");
        }
        catch (RegexMatchTimeoutException)
        {
            return (false, "regex_timeout");
        }
    }

    /// <summary>
    /// Resolves what a completed eval task cost in tokens, preferring the provider's own accounting.
    /// </summary>
    /// <param name="response">The agent's response, whose usage the model provider populates.</param>
    /// <param name="inputPrompt">The prompt sent, used only by the fallback estimate.</param>
    /// <param name="output">The text produced, used only by the fallback estimate.</param>
    /// <returns>The task's token cost. Never negative.</returns>
    /// <remarks>
    /// <para>
    /// Cost is one of the two things evaluation reports, and it feeds candidate selection: with it pinned
    /// to zero a candidate that solved every task by burning three times the context scored as identically
    /// cheap as one that solved them directly, so the cheaper agent could not win on the axis it wins on
    /// (issue #267).
    /// </para>
    /// <para>
    /// The provider's own figure is preferred because it is what actually gets billed, and it counts the
    /// whole request — the system prompt and every skill body the candidate loaded included, which is
    /// exactly the spending being compared. Providers populate usage inconsistently: some report a total,
    /// while others (the echo client used for offline runs among them) report input and output separately
    /// and leave the total unset, so both shapes are read before giving up.
    /// </para>
    /// <para>
    /// The fallback is the same characters-per-token estimate the context budget uses, over the prompt and
    /// the produced text only. It is a floor, not an equivalent: it cannot see the system prompt or the
    /// skill bodies, so a candidate's real spending is understated on that path. It is kept because a low
    /// figure derived from real text still orders candidates better than a zero that ranks them all equal.
    /// Taking it is logged, so a run whose costs look implausibly uniform can be checked rather than
    /// guessed at.
    /// </para>
    /// </remarks>
    private long ResolveTokenCost(AgentResponse? response, string inputPrompt, string output)
    {
        var billed = response?.Usage.TotalTokens() ?? 0;
        if (billed > 0)
            return billed;

        var estimated = TokenEstimationHelper.EstimateTokens(inputPrompt)
            + TokenEstimationHelper.EstimateTokens(output);

        _logger.LogDebug(
            "Model provider reported no usable token usage; falling back to an estimate of {EstimatedTokens} tokens",
            estimated);

        return estimated;
    }

    private static string ExtractContent(object? response)
    {
        if (response is null)
            return string.Empty;
        if (response is string str)
            return str;
        if (response is AgentResponse agentResponse)
            return agentResponse.Text ?? string.Empty;
        if (response is ChatResponse chatResponse)
        {
            return string.Join("\n", chatResponse.Messages
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents.OfType<TextContent>())
                .Select(tc => tc.Text));
        }

        return response.GetType().GetProperty("Content")?.GetValue(response)?.ToString()
            ?? response.ToString()
            ?? string.Empty;
    }
}
