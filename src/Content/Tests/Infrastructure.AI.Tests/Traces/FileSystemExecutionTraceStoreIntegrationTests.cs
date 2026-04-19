using System.Text.Json;
using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Traces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Traces;

/// <summary>
/// Integration tests for <see cref="FileSystemExecutionTraceStore"/> verifying
/// trace directory creation, manifest lifecycle, turn writing, trace appending,
/// scores writing, and the complete workflow.
/// </summary>
public sealed class FileSystemExecutionTraceStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemExecutionTraceStore _sut;
    private readonly Mock<ISecretRedactor> _redactor;

    public FileSystemExecutionTraceStoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trace-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _redactor = new Mock<ISecretRedactor>();
        _redactor.Setup(r => r.Redact(It.IsAny<string?>())).Returns<string?>(s => s);

        var appConfig = new AppConfig
        {
            MetaHarness = new MetaHarnessConfig
            {
                TraceDirectoryRoot = _tempDir,
                MaxFullPayloadKB = 10
            }
        };
        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _sut = new FileSystemExecutionTraceStore(
            options,
            _redactor.Object,
            NullLogger<FileSystemExecutionTraceStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static TraceScope CreateScope(Guid? runId = null, Guid? optRunId = null) => new()
    {
        ExecutionRunId = runId ?? Guid.NewGuid(),
        OptimizationRunId = optRunId ?? Guid.NewGuid(),
        CandidateId = Guid.NewGuid(),
        TaskId = "task-1"
    };

    [Fact]
    public async Task StartRunAsync_CreatesDirectoryAndManifest()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "TestAgent", StartedAt = DateTimeOffset.UtcNow };

        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        Directory.Exists(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, "manifest.json")).Should().BeTrue();
    }

    [Fact]
    public async Task StartRunAsync_ManifestContainsCorrectData()
    {
        var scope = CreateScope();
        var started = DateTimeOffset.UtcNow;
        var metadata = new RunMetadata { AgentName = "TestAgent", StartedAt = started };

        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "manifest.json"));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("agent_name").GetString().Should().Be("TestAgent");
        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task WriteTurnAsync_CreatesSystemPromptAndModelResponse()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var artifacts = new TurnArtifacts
        {
            SystemPrompt = "You are a helpful assistant.",
            ModelResponse = "Hello, how can I help?"
        };

        await writer.WriteTurnAsync(1, artifacts);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var turnDir = Path.Combine(dir, "turns", "1");
        Directory.Exists(turnDir).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(turnDir, "system_prompt.md")))
            .Should().Be("You are a helpful assistant.");
        (await File.ReadAllTextAsync(Path.Combine(turnDir, "model_response.md")))
            .Should().Be("Hello, how can I help?");
    }

    [Fact]
    public async Task WriteTurnAsync_WritesToolCallsJsonl()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var artifacts = new TurnArtifacts
        {
            ToolCallsJsonl = "{\"tool\":\"file_read\",\"args\":{\"path\":\"/tmp/test\"}}"
        };

        await writer.WriteTurnAsync(1, artifacts);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        File.Exists(Path.Combine(dir, "turns", "1", "tool_calls.jsonl")).Should().BeTrue();
    }

    [Fact]
    public async Task AppendTraceAsync_WritesToTracesJsonl()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var record = new ExecutionTraceRecord
        {
            Type = "tool_call",
            PayloadSummary = "Called file_read"
        };

        await writer.AppendTraceAsync(record);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var tracesFile = Path.Combine(dir, "traces.jsonl");
        File.Exists(tracesFile).Should().BeTrue();
        var lines = await File.ReadAllLinesAsync(tracesFile);
        lines.Should().ContainSingle();
    }

    [Fact]
    public async Task AppendTraceAsync_AssignsMonotonicSequence()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        await writer.AppendTraceAsync(new ExecutionTraceRecord { Type = "a", PayloadSummary = "first" });
        await writer.AppendTraceAsync(new ExecutionTraceRecord { Type = "b", PayloadSummary = "second" });

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "traces.jsonl"));
        lines.Should().HaveCount(2);

        var first = JsonDocument.Parse(lines[0]);
        var second = JsonDocument.Parse(lines[1]);
        first.RootElement.GetProperty("seq").GetInt64().Should().Be(1);
        second.RootElement.GetProperty("seq").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task AppendTraceAsync_TruncatesLongPayloadSummary()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var longSummary = new string('x', 1000);
        await writer.AppendTraceAsync(new ExecutionTraceRecord { Type = "test", PayloadSummary = longSummary });

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var line = (await File.ReadAllLinesAsync(Path.Combine(dir, "traces.jsonl")))[0];
        var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("payload_summary").GetString()!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task WriteScoresAsync_PersistsScoresFile()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var scores = new HarnessScores
        {
            PassRate = 0.85,
            TotalTokenCost = 1500
        };
        await writer.WriteScoresAsync(scores);

        var dir = await _sut.GetRunDirectoryAsync(scope);
        File.Exists(Path.Combine(dir, "scores.json")).Should().BeTrue();
    }

    [Fact]
    public async Task WriteSummaryAsync_PersistsSummaryFile()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        await writer.WriteSummaryAsync("# Summary\nAll tasks passed.");

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var content = await File.ReadAllTextAsync(Path.Combine(dir, "summary.md"));
        content.Should().Contain("All tasks passed");
    }

    [Fact]
    public async Task CompleteAsync_SetsWriteCompletedToTrue()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        await writer.CompleteAsync();

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var json = await File.ReadAllTextAsync(Path.Combine(dir, "manifest.json"));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WriteTurnAsync_InvalidTurnNumber_Throws()
    {
        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        var act = () => writer.WriteTurnAsync(0, new TurnArtifacts());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AppendTraceAsync_RedactsSecrets()
    {
        _redactor.Setup(r => r.Redact(It.IsAny<string?>()))
            .Returns("REDACTED_CONTENT");

        var scope = CreateScope();
        var metadata = new RunMetadata { AgentName = "Agent1", StartedAt = DateTimeOffset.UtcNow };
        await using var writer = await _sut.StartRunAsync(scope, metadata);

        await writer.AppendTraceAsync(new ExecutionTraceRecord
        {
            Type = "test",
            PayloadSummary = "secret-api-key-12345"
        });

        var dir = await _sut.GetRunDirectoryAsync(scope);
        var line = (await File.ReadAllLinesAsync(Path.Combine(dir, "traces.jsonl")))[0];
        line.Should().Contain("REDACTED_CONTENT");
    }
}
