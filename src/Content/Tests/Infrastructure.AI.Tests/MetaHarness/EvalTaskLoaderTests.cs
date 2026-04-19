using System.Text.Json;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Integration tests for <see cref="EvalTaskLoader"/> exercising directory loading,
/// valid/invalid JSON handling, and missing directory behavior.
/// Uses real temp directories and files.
/// </summary>
public sealed class EvalTaskLoaderTests : IDisposable
{
    private readonly string _root;
    private readonly ILogger _logger;

    public EvalTaskLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"eval-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _logger = NullLogger.Instance;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteTaskFile(string fileName, EvalTask task)
    {
        var json = JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(_root, fileName), json);
    }

    private void WriteRawFile(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_root, fileName), content);

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_ValidTasks_ReturnsAll()
    {
        WriteTaskFile("task1.json", new EvalTask
        {
            TaskId = "t1",
            Description = "Test basic greeting",
            InputPrompt = "Say hello",
            ExpectedOutputPattern = "hello"
        });
        WriteTaskFile("task2.json", new EvalTask
        {
            TaskId = "t2",
            Description = "Test math",
            InputPrompt = "What is 2+2?",
            ExpectedOutputPattern = "4",
            Tags = ["smoke", "math"]
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        result.Should().HaveCount(2);
        result.Should().Contain(t => t.TaskId == "t1");
        result.Should().Contain(t => t.TaskId == "t2");
    }

    [Fact]
    public void LoadFromDirectory_SingleTask_HasCorrectProperties()
    {
        WriteTaskFile("task.json", new EvalTask
        {
            TaskId = "single",
            Description = "Single task test",
            InputPrompt = "Do something",
            ExpectedOutputPattern = "done",
            Tags = ["regression"]
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        var task = result.Should().ContainSingle().Subject;
        task.TaskId.Should().Be("single");
        task.Description.Should().Be("Single task test");
        task.InputPrompt.Should().Be("Do something");
        task.ExpectedOutputPattern.Should().Be("done");
        task.Tags.Should().ContainSingle().Which.Should().Be("regression");
    }

    // ── Missing directory ────────────────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_NonExistentDirectory_ReturnsEmpty()
    {
        var result = EvalTaskLoader.LoadFromDirectory(
            Path.Combine(_root, "nonexistent"), _logger);

        result.Should().BeEmpty();
    }

    // ── Empty directory ──────────────────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_EmptyDirectory_ReturnsEmpty()
    {
        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        result.Should().BeEmpty();
    }

    // ── Invalid JSON is skipped ──────────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_InvalidJson_SkipsAndContinues()
    {
        WriteRawFile("bad.json", "not valid json {{{");
        WriteTaskFile("good.json", new EvalTask
        {
            TaskId = "good",
            Description = "Valid task",
            InputPrompt = "Test"
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        result.Should().ContainSingle().Which.TaskId.Should().Be("good");
    }

    [Fact]
    public void LoadFromDirectory_NullDeserializationResult_Skipped()
    {
        // "null" is valid JSON but deserializes to null
        WriteRawFile("null-task.json", "null");
        WriteTaskFile("valid.json", new EvalTask
        {
            TaskId = "valid",
            Description = "Valid",
            InputPrompt = "Test"
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        result.Should().ContainSingle().Which.TaskId.Should().Be("valid");
    }

    // ── Non-JSON files are ignored ───────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_NonJsonFiles_Ignored()
    {
        WriteRawFile("readme.txt", "This is not JSON");
        WriteRawFile("data.xml", "<task/>");
        WriteTaskFile("real.json", new EvalTask
        {
            TaskId = "real",
            Description = "Real task",
            InputPrompt = "Test"
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        result.Should().ContainSingle().Which.TaskId.Should().Be("real");
    }

    // ── Optional fields ──────────────────────────────────────────────────────

    [Fact]
    public void LoadFromDirectory_TaskWithNoPattern_LoadsSuccessfully()
    {
        WriteTaskFile("smoke.json", new EvalTask
        {
            TaskId = "smoke",
            Description = "Smoke test",
            InputPrompt = "Any response is fine"
            // No ExpectedOutputPattern — smoke test
        });

        var result = EvalTaskLoader.LoadFromDirectory(_root, _logger);

        var task = result.Should().ContainSingle().Subject;
        task.ExpectedOutputPattern.Should().BeNull();
        task.Tags.Should().BeEmpty();
    }
}
