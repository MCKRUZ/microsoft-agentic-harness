using System.Text.Json;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Loads <see cref="EvalTask"/> definitions from JSON files at the configured path.
/// Each file must deserialize to a single <see cref="EvalTask"/>.
/// Files that fail to deserialize are logged and skipped.
/// </summary>
public static class EvalTaskLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads all <c>*.json</c> files under <paramref name="directoryPath"/> and deserializes
    /// each as an <see cref="EvalTask"/>. Files that fail to deserialize are logged and skipped.
    /// Returns an empty list if the directory does not exist.
    /// </summary>
    /// <param name="directoryPath">Path to the directory containing eval task JSON files.</param>
    /// <param name="logger">Logger for warnings on missing directory or parse failures.</param>
    public static IReadOnlyList<EvalTask> LoadFromDirectory(string directoryPath, ILogger logger)
    {
        if (!Directory.Exists(directoryPath))
        {
            logger.LogWarning("Eval tasks directory not found: {Path}", directoryPath);
            return [];
        }

        var results = new List<EvalTask>();

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file);
                var task = JsonSerializer.Deserialize<EvalTask>(json, JsonOptions);
                if (task is not null)
                    results.Add(task);
                else
                    logger.LogWarning("Eval task file deserialized as null, skipping: {File}", file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize eval task from {File}, skipping", file);
            }
        }

        logger.LogInformation("Loaded {Count} eval task(s) from {Path}", results.Count, directoryPath);
        return results;
    }
}
