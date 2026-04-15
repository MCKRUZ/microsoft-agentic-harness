using System.Text.Json;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

public sealed class FileSystemRegressionSuiteServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemRegressionSuiteService _sut;

    public FileSystemRegressionSuiteServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        var config = new MetaHarnessConfig { RegressionSuiteThreshold = 0.8 };
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
        _sut = new FileSystemRegressionSuiteService(opts, NullLogger<FileSystemRegressionSuiteService>.Instance);
    }

    [Fact]
    public async Task LoadAsync_FileNotExists_ReturnsEmptySuite()
    {
        var suite = await _sut.LoadAsync(_tempDir);

        Assert.Empty(suite.TaskIds);
        Assert.Equal(0.8, suite.Threshold);
    }

    [Fact]
    public async Task LoadAsync_ValidFile_ReturnsPopulatedSuite()
    {
        var expected = new RegressionSuite
        {
            TaskIds = ["task-1", "task-2"],
            Threshold = 0.9,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "regression_suite.json"), json);

        var suite = await _sut.LoadAsync(_tempDir);

        Assert.Equal(expected.TaskIds, suite.TaskIds);
        Assert.Equal(0.9, suite.Threshold);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsEmptySuite()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "regression_suite.json"), "not-valid-json{{{{");

        var suite = await _sut.LoadAsync(_tempDir);

        Assert.Empty(suite.TaskIds);
    }

    [Fact]
    public void Check_EmptySuite_ReturnsPassedTrue()
    {
        var suite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var evalResult = new EvaluationResult(Guid.NewGuid(), 0.5, 100, []);

        var result = _sut.Check(suite, evalResult);

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.PassRate);
        Assert.Empty(result.FailedTaskIds);
    }

    [Fact]
    public void Check_AllRegressionTasksPass_ReturnsPassedTrue()
    {
        var suite = new RegressionSuite { TaskIds = ["task-a", "task-b"], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var evalResult = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-a", true, 10),
            new TaskEvaluationResult("task-b", true, 10),
        ]);

        var result = _sut.Check(suite, evalResult);

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.PassRate);
        Assert.Empty(result.FailedTaskIds);
    }

    [Fact]
    public void Check_BelowThreshold_ReturnsPassedFalse()
    {
        // 1 of 2 tasks pass = 50%, threshold = 0.8
        var suite = new RegressionSuite { TaskIds = ["task-a", "task-b"], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var evalResult = new EvaluationResult(Guid.NewGuid(), 0.5, 100,
        [
            new TaskEvaluationResult("task-a", true, 10),
            new TaskEvaluationResult("task-b", false, 10),
        ]);

        var result = _sut.Check(suite, evalResult);

        Assert.False(result.Passed);
        Assert.Equal(0.5, result.PassRate);
        Assert.Contains("task-b", result.FailedTaskIds);
    }

    [Fact]
    public void Check_RegressionTaskNotInEvalResults_TreatsAsFailed()
    {
        var suite = new RegressionSuite { TaskIds = ["task-a", "task-missing"], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var evalResult = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-a", true, 10),
            // task-missing absent
        ]);

        var result = _sut.Check(suite, evalResult);

        Assert.False(result.Passed); // 1/2 = 50%, below 80% threshold
        Assert.Contains("task-missing", result.FailedTaskIds);
    }

    [Fact]
    public async Task PromoteAsync_NoPreviousResults_PromotesAllPassingTasks()
    {
        var suite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var current = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
            new TaskEvaluationResult("task-2", false, 10),
        ]);

        var updated = await _sut.PromoteAsync(suite, current, null, _tempDir);

        Assert.Contains("task-1", updated.TaskIds);
        Assert.DoesNotContain("task-2", updated.TaskIds);
    }

    [Fact]
    public async Task PromoteAsync_NewlyFixedTasks_AddedToSuite()
    {
        var suite = new RegressionSuite { TaskIds = ["task-1"], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var previous = new EvaluationResult(Guid.NewGuid(), 0.5, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
            new TaskEvaluationResult("task-2", false, 10), // was failing
        ]);
        var current = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
            new TaskEvaluationResult("task-2", true, 10), // now passing
        ]);

        var updated = await _sut.PromoteAsync(suite, current, previous, _tempDir);

        Assert.Contains("task-1", updated.TaskIds);
        Assert.Contains("task-2", updated.TaskIds);
    }

    [Fact]
    public async Task PromoteAsync_NoNewlyFixedTasks_SuiteUnchanged()
    {
        var suite = new RegressionSuite { TaskIds = ["task-1"], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var previous = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
        ]);
        var current = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
        ]);

        var updated = await _sut.PromoteAsync(suite, current, previous, _tempDir);

        Assert.Single(updated.TaskIds);
        Assert.Equal("task-1", updated.TaskIds[0]);
    }

    [Fact]
    public async Task PromoteAsync_PersistsToFile_AfterPromotion()
    {
        var suite = new RegressionSuite { TaskIds = [], Threshold = 0.8, LastUpdatedAt = DateTimeOffset.UtcNow };
        var current = new EvaluationResult(Guid.NewGuid(), 1.0, 100,
        [
            new TaskEvaluationResult("task-1", true, 10),
        ]);

        await _sut.PromoteAsync(suite, current, null, _tempDir);

        var filePath = Path.Combine(_tempDir, "regression_suite.json");
        Assert.True(File.Exists(filePath));

        var json = await File.ReadAllTextAsync(filePath);
        var loaded = JsonSerializer.Deserialize<RegressionSuite>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(loaded);
        Assert.Contains("task-1", loaded.TaskIds);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }
}
