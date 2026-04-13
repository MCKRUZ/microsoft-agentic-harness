using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Traces;
using Domain.AI.Agents;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.Skills;
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
    private readonly ILogger<AgentEvaluationService> _logger;

    public AgentEvaluationService(
        IOptionsMonitor<MetaHarnessConfig> config,
        IExecutionTraceStore traceStore,
        IAgentFactory agentFactory,
        ILogger<AgentEvaluationService> logger)
    {
        _config = config;
        _traceStore = traceStore;
        _agentFactory = agentFactory;
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

        var candidateProvider = new CandidateSkillContentProvider(candidate.Snapshot.SkillFileSnapshots);

        var metadata = new RunMetadata
        {
            AgentName = "EvaluationAgent",
            StartedAt = DateTimeOffset.UtcNow
        };

        ITraceWriter? traceWriter = null;
        var traceCompleted = false;
        TaskEvaluationResult? taskResult = null;

        try
        {
            traceWriter = await _traceStore.StartRunAsync(scope, metadata, cancellationToken);

            var context = new AgentExecutionContext
            {
                Name = "EvaluationAgent",
                Instruction = candidate.Snapshot.SystemPromptSnapshot,
                DeploymentName = string.IsNullOrEmpty(cfg.EvaluationModelVersion) ? null : cfg.EvaluationModelVersion,
                TraceScope = scope,
                AdditionalProperties = new Dictionary<string, object>
                {
                    [ISkillContentProvider.AdditionalPropertiesKey] = candidateProvider,
                    [ITraceWriter.AdditionalPropertiesKey] = traceWriter
                }
            };

            var agent = await _agentFactory.CreateAgentAsync(context, cancellationToken);
            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, task.InputPrompt)],
                cancellationToken: cancellationToken);

            var output = ExtractContent(response);
            var (passed, failureReason) = Grade(output, task.ExpectedOutputPattern);
            taskResult = new TaskEvaluationResult(task.TaskId, passed, TokenCost: 0L, failureReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Task {TaskId} failed for candidate {CandidateId}",
                task.TaskId, candidate.CandidateId);
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
        }

        return taskResult!;
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
