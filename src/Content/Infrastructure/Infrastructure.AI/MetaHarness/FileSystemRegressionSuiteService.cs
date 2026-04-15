using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MetaHarness;

/// <summary>
/// Filesystem-backed implementation of <see cref="IRegressionSuiteService"/>.
/// </summary>
/// <remarks>
/// Stores the regression suite as <c>regression_suite.json</c> in the optimization run directory
/// using atomic temp-file + rename writes, consistent with other meta-harness file artifacts.
/// </remarks>
public sealed class FileSystemRegressionSuiteService : IRegressionSuiteService
{
    private const string FileName = "regression_suite.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<FileSystemRegressionSuiteService> _logger;

    /// <summary>Initializes a new instance of <see cref="FileSystemRegressionSuiteService"/>.</summary>
    public FileSystemRegressionSuiteService(
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<FileSystemRegressionSuiteService> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<RegressionSuite> LoadAsync(string runDirectoryPath, CancellationToken ct = default)
    {
        var path = Path.Combine(runDirectoryPath, FileName);
        if (!File.Exists(path))
            return EmptySuite();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var suite = JsonSerializer.Deserialize<RegressionSuite>(json, JsonOptions);
            return suite ?? EmptySuite();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Corrupt {File} in '{Dir}' — returning empty suite", FileName, runDirectoryPath);
            return EmptySuite();
        }
    }

    /// <inheritdoc/>
    public RegressionCheckResult Check(RegressionSuite suite, EvaluationResult evalResult)
    {
        if (suite.TaskIds.Count == 0)
        {
            return new RegressionCheckResult
            {
                Passed = true,
                PassRate = 1.0,
                FailedTaskIds = [],
            };
        }

        var evalLookup = evalResult.PerExampleResults
            .ToDictionary(r => r.TaskId, r => r.Passed);

        var failedIds = new List<string>();
        foreach (var taskId in suite.TaskIds)
        {
            if (!evalLookup.TryGetValue(taskId, out var passed) || !passed)
                failedIds.Add(taskId);
        }

        var passRate = (double)(suite.TaskIds.Count - failedIds.Count) / suite.TaskIds.Count;

        return new RegressionCheckResult
        {
            Passed = passRate >= suite.Threshold,
            PassRate = passRate,
            FailedTaskIds = failedIds,
        };
    }

    /// <inheritdoc/>
    public async Task<RegressionSuite> PromoteAsync(
        RegressionSuite suite,
        EvaluationResult currentResults,
        EvaluationResult? previousBestResults,
        string runDirectoryPath,
        CancellationToken ct = default)
    {
        HashSet<string> newlyFixed;

        if (previousBestResults is null)
        {
            // First winning iteration — seed the suite with all currently-passing tasks
            newlyFixed = currentResults.PerExampleResults
                .Where(r => r.Passed)
                .Select(r => r.TaskId)
                .ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            var previousFailed = previousBestResults.PerExampleResults
                .Where(r => !r.Passed)
                .Select(r => r.TaskId)
                .ToHashSet(StringComparer.Ordinal);

            newlyFixed = currentResults.PerExampleResults
                .Where(r => r.Passed && previousFailed.Contains(r.TaskId))
                .Select(r => r.TaskId)
                .ToHashSet(StringComparer.Ordinal);
        }

        if (newlyFixed.Count == 0)
            return suite;

        var merged = new HashSet<string>(suite.TaskIds, StringComparer.Ordinal);
        merged.UnionWith(newlyFixed);

        var updated = new RegressionSuite
        {
            TaskIds = merged.Order(StringComparer.Ordinal).ToList(),
            Threshold = suite.Threshold,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        var filePath = Path.Combine(runDirectoryPath, FileName);
        var tmp = filePath + ".tmp";
        var json = JsonSerializer.Serialize(updated, JsonOptions);
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, filePath, overwrite: true);

        _logger.LogInformation(
            "Regression suite: promoted {Count} task(s); suite now has {Total} task(s)",
            newlyFixed.Count, updated.TaskIds.Count);

        return updated;
    }

    private RegressionSuite EmptySuite() => new()
    {
        TaskIds = [],
        Threshold = _config.CurrentValue.RegressionSuiteThreshold,
        LastUpdatedAt = DateTimeOffset.UtcNow,
    };
}
