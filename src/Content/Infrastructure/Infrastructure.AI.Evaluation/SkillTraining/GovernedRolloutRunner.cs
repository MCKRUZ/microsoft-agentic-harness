using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.Evaluation;
using Domain.AI.SkillTraining;
using Domain.Common.Config.AI.SkillTraining;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Evaluation.SkillTraining;

/// <summary>
/// The skill-training loop's rollout runner, built on the harness's own eval framework (#843): items come
/// from an eval dataset, each is run as a real agent turn through <see cref="IAgentInvoker"/> (so content
/// safety, governance and audit apply exactly as for any turn), and the result is scored by the case's
/// own registered metrics.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a rollout is.</b> The configured rollout agent runs the case's input with the candidate skill
/// document in place of its system prompt. <c>Hard</c> and <c>Soft</c> come from the case's metrics
/// (<see cref="RolloutScoring"/>); the trajectory is what the agent did (<see cref="RolloutTrajectory"/>)
/// and never contains the case's expected output or any score.
/// </para>
/// <para>
/// <b>What it refuses.</b> A dataset case that sets its own <c>system_prompt</c>, <c>agent_name</c> or
/// router <c>target</c> override would replace the candidate or bypass the agent, and the rollout would
/// measure nothing about it; the batch is refused rather than run. So is a case with no metrics, a metric key nothing is registered under, a
/// dataset with no <c>split:</c> tags and a pinned item that is not in the split. Everything is checked
/// before the first agent turn so a misconfiguration costs no tokens.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped: the dataset is read once per instance, so one training run sees one stable
/// item pool even if the file changes on disk between epochs.
/// </para>
/// </remarks>
public sealed class GovernedRolloutRunner : IRolloutRunner
{
    private const string AgentNameKey = "agent_name";
    private const string SystemPromptKey = "system_prompt";
    private const string TargetKey = "target";

    private readonly IAgentInvoker _invoker;
    private readonly IReadOnlyDictionary<string, IEvalMetric> _metricsByKey;
    private readonly IReadOnlyList<IEvalDatasetLoader> _loaders;
    private readonly IOptionsMonitor<SkillTrainingConfig> _config;
    private readonly ILogger<GovernedRolloutRunner> _logger;
    private readonly SemaphoreSlim _datasetGate = new(1, 1);
    private EvalDataset? _dataset;

    /// <summary>Initializes the runner.</summary>
    public GovernedRolloutRunner(
        IAgentInvoker invoker,
        IEnumerable<IEvalMetric> metrics,
        IEnumerable<IEvalDatasetLoader> loaders,
        IOptionsMonitor<SkillTrainingConfig> config,
        ILogger<GovernedRolloutRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loaders);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _invoker = invoker;
        _metricsByKey = metrics.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        _loaders = [.. loaders];
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RolloutResult>> RunAsync(
        string skillContent, RolloutBatch batch, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillContent);
        ArgumentNullException.ThrowIfNull(batch);

        var config = _config.CurrentValue;
        if (string.IsNullOrWhiteSpace(config.DatasetPath) || string.IsNullOrWhiteSpace(config.RolloutAgentName))
            throw new InvalidOperationException(
                "skill_training.rollout_not_configured: set AppConfig:AI:SkillTraining:DatasetPath and RolloutAgentName.");

        var dataset = await LoadDatasetAsync(config.DatasetPath, cancellationToken).ConfigureAwait(false);
        var cases = RolloutItemSelector.Select(dataset, batch);
        ValidateCases(cases);

        var overrides = new Dictionary<string, string>
        {
            [AgentNameKey] = config.RolloutAgentName,
            [SystemPromptKey] = skillContent
        };

        using var gate = new SemaphoreSlim(config.MaxRolloutConcurrency);
        var tasks = cases.Select(c => RunOneAsync(c, overrides, gate, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<RolloutResult> RunOneAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string> overrides,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var invocation = await InvokeAsync(@case, overrides, cancellationToken).ConfigureAwait(false);
            var scores = invocation.Success
                ? await ScoreAsync(@case, invocation, cancellationToken).ConfigureAwait(false)
                : [];
            var (hard, soft) = RolloutScoring.Combine(invocation.Success, scores);

            return new RolloutResult
            {
                ItemId = @case.Id,
                Hard = hard,
                Soft = soft,
                Trajectory = RolloutTrajectory.Render(@case, invocation),
                TaskType = @case.Tags.FirstOrDefault(t => !RolloutItemSelector.IsSplitTag(t)) ?? string.Empty
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case, IReadOnlyDictionary<string, string> overrides, CancellationToken cancellationToken)
    {
        try
        {
            return await _invoker.InvokeAsync(@case, overrides, forceDeterministic: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One rollout that cannot run is a failed rollout, not a failed batch. The detail goes to the
            // log; the trajectory says only that the run did not finish.
            _logger.LogWarning(ex, "Skill-training rollout {CaseId} could not be invoked", @case.Id);
            return new AgentInvocationResult { Success = false, Output = string.Empty };
        }
    }

    private async Task<IReadOnlyList<MetricScore>> ScoreAsync(
        EvalCase @case, AgentInvocationResult invocation, CancellationToken cancellationToken)
    {
        var scores = new List<MetricScore>(@case.MetricSpecs.Count);
        foreach (var spec in @case.MetricSpecs)
        {
            try
            {
                scores.Add(await _metricsByKey[spec.MetricKey]
                    .ScoreAsync(@case, invocation, spec, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The same fail-soft rule the eval runner applies: a metric that throws did not pass.
                _logger.LogWarning(ex, "Metric {MetricKey} threw on skill-training case {CaseId}", spec.MetricKey, @case.Id);
                scores.Add(new MetricScore { MetricKey = spec.MetricKey, Score = 0.0, Verdict = Verdict.Warn });
            }
        }

        return scores;
    }

    private void ValidateCases(IReadOnlyList<EvalCase> cases)
    {
        foreach (var @case in cases)
        {
            var clash = @case.InvocationOverrides.Keys.FirstOrDefault(k =>
                string.Equals(k, SystemPromptKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, AgentNameKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, TargetKey, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
                throw new InvalidOperationException(
                    $"skill_training.rollout_case_overrides_candidate: case '{@case.Id}' sets '{clash}', which would replace the candidate skill or bypass the agent.");

            if (@case.MetricSpecs.Count == 0)
                throw new InvalidOperationException(
                    $"skill_training.rollout_case_unscorable: case '{@case.Id}' declares no metrics.");

            var unknown = @case.MetricSpecs.FirstOrDefault(s => !_metricsByKey.ContainsKey(s.MetricKey));
            if (unknown is not null)
                throw new InvalidOperationException(
                    $"skill_training.rollout_metric_unknown: case '{@case.Id}' names metric '{unknown.MetricKey}', which is not registered.");
        }
    }

    private async Task<EvalDataset> LoadDatasetAsync(string path, CancellationToken cancellationToken)
    {
        if (_dataset is not null)
            return _dataset;

        await _datasetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_dataset is not null)
                return _dataset;

            var extension = Path.GetExtension(path).TrimStart('.');
            var loader = _loaders.FirstOrDefault(l => l.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"skill_training.rollout_dataset_format: no dataset loader handles '.{extension}' files.");

            try
            {
                _dataset = await loader.LoadAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or DirectoryNotFoundException)
            {
                _logger.LogError(ex, "Skill-training dataset could not be loaded from {Path}", path);
                throw new InvalidOperationException("skill_training.rollout_dataset_unreadable: the dataset could not be loaded.");
            }

            return _dataset;
        }
        finally
        {
            _datasetGate.Release();
        }
    }
}
