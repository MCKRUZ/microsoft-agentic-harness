using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Traces;
using Domain.Common.Config;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Traces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace Infrastructure.AI.Tests.Traces;

public sealed class FileSystemExecutionTraceStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemExecutionTraceStore _sut;
    private readonly Mock<ISecretRedactor> _redactor;

    public FileSystemExecutionTraceStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "trace-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _redactor = new Mock<ISecretRedactor>();
        _redactor.Setup(r => r.Redact(It.IsAny<string?>())).Returns<string?>(s => s); // passthrough

        var config = new AppConfig
        {
            MetaHarness = new MetaHarnessConfig
            {
                TraceDirectoryRoot = _tempDir,
                MaxFullPayloadKB = 1 // 1 KB for tests
            }
        };

        _sut = new FileSystemExecutionTraceStore(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == config),
            _redactor.Object,
            NullLogger<FileSystemExecutionTraceStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup
        }
    }

    private static RunMetadata DefaultMetadata(string agentName = "test-agent") => new()
    {
        AgentName = agentName,
        StartedAt = DateTimeOffset.UtcNow
    };

    // --- Directory creation ---

    [Fact]
    public async Task StartRunAsync_WhenNoOptimizationId_CreatesRunDirectoryUnderExecutions()
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());

        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        writer.RunDirectory.Should().StartWith(Path.Combine(_tempDir, "executions"));
        Directory.Exists(writer.RunDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task StartRunAsync_WhenOptimizationIdProvided_CreatesRunDirectoryUnderOptimizations()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid()
        };

        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        writer.RunDirectory.Should().Contain("optimizations");
        Directory.Exists(writer.RunDirectory).Should().BeTrue();
    }

    // --- Manifest ---

    [Fact]
    public async Task StartRunAsync_WritesManifestJson_WithWriteCompletedFalse()
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());

        var writer = await _sut.StartRunAsync(scope, DefaultMetadata("my-agent"));

        var manifestPath = Path.Combine(writer.RunDirectory, "manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("agent_name").GetString().Should().Be("my-agent");
    }

    [Fact]
    public async Task StartRunAsync_ManifestJson_ContainsExecutionRunId()
    {
        var runId = Guid.NewGuid();
        var scope = TraceScope.ForExecution(runId);

        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        var json = await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "manifest.json"));
        json.Should().Contain(runId.ToString("D"));
    }

    // --- Turn artifacts ---

    [Fact]
    public async Task WriteTurnAsync_CreatesExpectedSubdirectoryWithArtifactFiles()
    {
        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
        var artifacts = new TurnArtifacts
        {
            TurnNumber = 1,
            SystemPrompt = "You are a test agent.",
            ModelResponse = "Hello, I am ready.",
            ToolCallsJsonl = "{\"name\":\"file_read\"}",
            StateSnapshot = "{\"step\":1}"
        };

        await writer.WriteTurnAsync(1, artifacts);

        var turnDir = Path.Combine(writer.RunDirectory, "turns", "1");
        Directory.Exists(turnDir).Should().BeTrue();
        File.Exists(Path.Combine(turnDir, "system_prompt.md")).Should().BeTrue();
        File.Exists(Path.Combine(turnDir, "model_response.md")).Should().BeTrue();
        File.Exists(Path.Combine(turnDir, "tool_calls.jsonl")).Should().BeTrue();
        File.Exists(Path.Combine(turnDir, "state_snapshot.json")).Should().BeTrue();
    }

    [Fact]
    public async Task WriteTurnAsync_AppliesSecretRedactor_ToSystemPrompt()
    {
        _redactor.Setup(r => r.Redact("secret-prompt")).Returns("[REDACTED]");

        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
        var artifacts = new TurnArtifacts { TurnNumber = 1, SystemPrompt = "secret-prompt" };

        await writer.WriteTurnAsync(1, artifacts);

        var content = await File.ReadAllTextAsync(
            Path.Combine(writer.RunDirectory, "turns", "1", "system_prompt.md"));
        content.Should().Be("[REDACTED]");
    }

    // --- JSONL trace appending ---

    [Fact]
    public async Task AppendTraceAsync_WritesValidJsonlLine_ToTracesFile()
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());
        var record = new ExecutionTraceRecord
        {
            Type = TraceRecordTypes.Observation,
            ExecutionRunId = scope.ExecutionRunId.ToString("D"),
            TurnId = Guid.NewGuid().ToString("D"),
            PayloadSummary = "test payload"
        };

        await writer.AppendTraceAsync(record);

        var tracesPath = Path.Combine(writer.RunDirectory, "traces.jsonl");
        File.Exists(tracesPath).Should().BeTrue();
        var lines = await File.ReadAllLinesAsync(tracesPath);
        lines.Should().HaveCount(1);

        using var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("type").GetString().Should().Be(TraceRecordTypes.Observation);
    }

    [Fact]
    public async Task AppendTraceAsync_AssignsMonotonicallyIncreasingSeqNumbers()
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        for (var i = 0; i < 5; i++)
        {
            await writer.AppendTraceAsync(new ExecutionTraceRecord
            {
                Type = TraceRecordTypes.Observation,
                ExecutionRunId = scope.ExecutionRunId.ToString("D"),
                TurnId = "turn-1"
            });
        }

        var lines = await File.ReadAllLinesAsync(Path.Combine(writer.RunDirectory, "traces.jsonl"));
        var seqNumbers = lines
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64())
            .ToList();

        seqNumbers.Should().BeInAscendingOrder();
        seqNumbers.Distinct().Should().HaveCount(5, "sequence numbers must be unique");
    }

    [Fact]
    public async Task AppendTraceAsync_ConcurrentWrites_DoNotCorruptJsonl()
    {
        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        var tasks = Enumerable.Range(0, 10).Select(taskIdx =>
            Task.Run(async () =>
            {
                for (var j = 0; j < 20; j++)
                {
                    await writer.AppendTraceAsync(new ExecutionTraceRecord
                    {
                        Type = TraceRecordTypes.Observation,
                        ExecutionRunId = scope.ExecutionRunId.ToString("D"),
                        TurnId = $"task-{taskIdx}-turn-{j}"
                    });
                }
            }));

        await Task.WhenAll(tasks);

        var tracesPath = Path.Combine(writer.RunDirectory, "traces.jsonl");
        var lines = await File.ReadAllLinesAsync(tracesPath);
        lines.Should().HaveCount(200, "all 10*20 writes must be present");

        // Every line must be valid JSON and seq numbers must be unique
        var seqNumbers = new List<long>(200);
        foreach (var line in lines)
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow("every line must be valid JSON");
            var seq = JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64();
            seqNumbers.Add(seq);
        }

        seqNumbers.Distinct().Should().HaveCount(200, "no duplicate sequence numbers");
    }

    // --- Redaction in trace records ---

    [Fact]
    public async Task AppendTraceAsync_AppliesRedaction_WhenPayloadContainsSecret()
    {
        _redactor.Setup(r => r.Redact("sk-secret-key")).Returns("[REDACTED]");

        var scope = TraceScope.ForExecution(Guid.NewGuid());
        var writer = await _sut.StartRunAsync(scope, DefaultMetadata());

        await writer.AppendTraceAsync(new ExecutionTraceRecord
        {
            Type = TraceRecordTypes.ToolResult,
            ExecutionRunId = scope.ExecutionRunId.ToString("D"),
            TurnId = "t1",
            PayloadSummary = "sk-secret-key"
        });

        var line = (await File.ReadAllLinesAsync(Path.Combine(writer.RunDirectory, "traces.jsonl")))[0];
        line.Should().Contain("[REDACTED]");
        line.Should().NotContain("sk-secret-key");
    }

    // --- Large payload splitting ---

    [Fact]
    public async Task WriteTurnAsync_LargeToolResult_WritesToToolResultsDirectory()
    {
        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
        var largePayload = new string('x', 2 * 1024); // 2 KB > MaxFullPayloadKB of 1 KB

        var artifacts = new TurnArtifacts
        {
            TurnNumber = 1,
            ToolResults = new Dictionary<string, string>
            {
                ["call-abc"] = largePayload
            }
        };

        await writer.WriteTurnAsync(1, artifacts);

        var toolResultFile = Path.Combine(writer.RunDirectory, "turns", "1", "tool_results", "call-abc.json");
        File.Exists(toolResultFile).Should().BeTrue();
        var content = await File.ReadAllTextAsync(toolResultFile);
        content.Should().Be(largePayload);
    }

    // --- Atomic writes ---

    [Fact]
    public async Task WriteScoresAsync_WritesScoresJson_WithValidContent()
    {
        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());
        var scores = new HarnessScores
        {
            PassRate = 0.85,
            TotalTokenCost = 1500,
            ScoredAt = DateTimeOffset.UtcNow
        };

        await writer.WriteScoresAsync(scores);

        var scoresPath = Path.Combine(writer.RunDirectory, "scores.json");
        File.Exists(scoresPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(scoresPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("pass_rate").GetDouble().Should().BeApproximately(0.85, 0.001);
    }

    // --- CompleteAsync ---

    [Fact]
    public async Task CompleteAsync_SetsWriteCompletedTrue_InManifest()
    {
        var writer = await _sut.StartRunAsync(TraceScope.ForExecution(Guid.NewGuid()), DefaultMetadata());

        await writer.CompleteAsync();

        var json = await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "manifest.json"));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("write_completed").GetBoolean().Should().BeTrue();
    }

    // --- GetRunDirectoryAsync ---

    [Fact]
    public async Task GetRunDirectoryAsync_ReturnsCorrectAbsolutePath()
    {
        var runId = Guid.NewGuid();
        var scope = TraceScope.ForExecution(runId);

        var dir = await _sut.GetRunDirectoryAsync(scope);

        dir.Should().StartWith(_tempDir);
        dir.Should().Contain(runId.ToString("D").ToLowerInvariant());
        // GetRunDirectoryAsync does NOT create the directory
        Directory.Exists(dir).Should().BeFalse();
    }
}
