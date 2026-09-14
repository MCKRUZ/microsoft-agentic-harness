using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Application.Common.Interfaces.Common;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;

namespace Application.Core.CQRS.MetaHarness;

public sealed partial class RunHarnessOptimizationCommandHandler
{
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

    private static void WriteSnapshotFiles(
        string runDir,
        HarnessCandidate candidate,
        IOwnerOnlyDirectoryCreator directoryCreator,
        ILogger logger)
    {
        var snapshotDir = Path.Combine(
            runDir, "candidates", candidate.CandidateId.ToString(), "snapshot");
        directoryCreator.Create(snapshotDir, logger);

        foreach (var (relativePath, content) in candidate.Snapshot.SkillFileSnapshots)
        {
            var filePath = SafeResolvePath(snapshotDir, relativePath);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
                directoryCreator.Create(dir, logger);
            File.WriteAllText(filePath, content);
        }
    }

    private static void WriteProposedSnapshot(
        string proposedDir,
        HarnessCandidate? best,
        IOwnerOnlyDirectoryCreator directoryCreator,
        ILogger logger)
    {
        if (best is null)
            return;

        if (Directory.Exists(proposedDir))
            Directory.Delete(proposedDir, recursive: true);
        directoryCreator.Create(proposedDir, logger);

        foreach (var (relativePath, content) in best.Snapshot.SkillFileSnapshots)
        {
            var filePath = SafeResolvePath(proposedDir, relativePath);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
                directoryCreator.Create(dir, logger);
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
}
