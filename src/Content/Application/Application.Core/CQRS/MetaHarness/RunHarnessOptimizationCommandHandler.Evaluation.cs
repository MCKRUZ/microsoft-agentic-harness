using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.MetaHarness;

public sealed partial class RunHarnessOptimizationCommandHandler
{
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

    /// <summary>
    /// Returns true when <paramref name="candidate"/> should replace <paramref name="currentBest"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="currentBest"/> is never null: a run always compares against something measured, the evaluated
    /// seed or the best accepted so far (issue #1075).
    /// Tie-breaking order:
    /// <list type="number">
    ///   <item>Pass rate exceeds current best by more than <paramref name="threshold"/> → clear improvement.</item>
    ///   <item>Pass rate within threshold (tie) → lower token cost wins.</item>
    ///   <item>Pass rate and cost both tied → lower iteration wins (keep earlier candidate).</item>
    /// </list>
    /// </remarks>
    private static bool IsBetter(
        HarnessCandidate candidate,
        HarnessCandidate currentBest,
        double threshold)
    {
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

    private static async Task<string?> ReadLearningsFileAsync(string runDir)
    {
        var path = Path.Combine(runDir, "learnings.md");
        if (!File.Exists(path))
            return null;
        var content = await File.ReadAllTextAsync(path);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private static async Task AppendLearningsAsync(string runDir, string entry)
    {
        var path = Path.Combine(runDir, "learnings.md");
        await File.AppendAllTextAsync(path, entry);
    }

    private static string BuildLearningsEntry(
        int iteration,
        HarnessProposal proposal,
        EvaluationResult evalResult,
        bool acceptedAsBest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Iteration {iteration}");
        sb.AppendLine($"- **Status**: {(acceptedAsBest ? "ACCEPTED as new best" : "Not accepted")}");
        sb.AppendLine($"- **Pass rate**: {evalResult.PassRate:P2}");
        sb.AppendLine($"- **Token cost**: {evalResult.TotalTokenCost}");
        sb.AppendLine($"- **Reasoning**: {proposal.Reasoning}");
        if (!string.IsNullOrWhiteSpace(proposal.Learnings))
            sb.AppendLine($"- **Proposer observations**: {proposal.Learnings}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildFailedLearningsEntry(int iteration, string failureReason)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Iteration {iteration}");
        sb.AppendLine("- **Status**: FAILED");
        sb.AppendLine($"- **Failure**: {failureReason}");
        sb.AppendLine();
        return sb.ToString();
    }
}
