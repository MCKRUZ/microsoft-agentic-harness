using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Tests.Tools;
using Infrastructure.AI.Traces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Traces;

/// <summary>
/// Trace runs age out by their newest file (#901), and the sweep deletes only what the trace writer wrote: a file anything
/// else put in a run, an optimization's candidate definition, and whatever a link points at all survive.
/// </summary>
public sealed class FileSystemExecutionTraceStoreRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddDays(-30);
    private static readonly DateTime Old = Now.AddDays(-45).UtcDateTime;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"trace-retention-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"trace-retention-outside-{Guid.NewGuid():N}");
    private readonly AppConfig _config = new();
    private readonly FileSystemExecutionTraceStore _sut;

    public FileSystemExecutionTraceStoreRetentionTests()
    {
        _config.MetaHarness.TraceDirectoryRoot = _root;
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string?>())).Returns<string?>(s => s);
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        _sut = new FileSystemExecutionTraceStore(
            monitor.Object, redactor.Object, NullLogger<FileSystemExecutionTraceStore>.Instance);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task ARunNothingHasWrittenToSinceTheCutoff_IsDeleted_AndARecentOneIsKept()
    {
        var old = await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid()));
        var recent = await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid()));
        Age(old, Old);

        var deleted = await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        deleted.Should().Be(1);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
    }

    [Fact]
    public async Task ARunWithOneRecentFile_IsKept_HoweverOldItsManifest()
    {
        // A long run whose first files are old is still being written; its age is its newest file.
        var run = await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid()));
        Age(run, Old);
        File.SetLastWriteTimeUtc(Path.Combine(run, "traces.jsonl"), Now.UtcDateTime);

        var deleted = await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(Path.Combine(run, "manifest.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AFileTheWriterDidNotCreate_SurvivesWithItsFolder()
    {
        var run = await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid()));
        var foreign = Path.Combine(run, "turns", "1", "notes.txt");
        await File.WriteAllTextAsync(foreign, "an agent wrote this here");
        Age(run, Old);

        await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        File.Exists(foreign).Should().BeTrue();
        File.Exists(Path.Combine(run, "manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(run, "turns", "1", "model_response.md")).Should().BeFalse();
    }

    [Fact]
    public async Task AnOptimizationsCandidateDefinition_SurvivesItsEvaluationRuns()
    {
        var optimization = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var run = await WriteRunAsync(new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(), OptimizationRunId = optimization, CandidateId = candidate, TaskId = "task-1",
        });
        var candidateDir = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(), OptimizationRunId = optimization, CandidateId = candidate,
        }.ResolveDirectory(_root);
        var definition = Path.Combine(candidateDir, "candidate.json");
        await File.WriteAllTextAsync(definition, "{}");
        Age(Path.Combine(_root, "optimizations"), Old);

        var deleted = await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        deleted.Should().Be(1);
        Directory.Exists(run).Should().BeFalse();
        Directory.Exists(Path.Combine(candidateDir, "eval")).Should().BeFalse("emptied folders above a run go with it");
        File.Exists(definition).Should().BeTrue();
    }

    [Fact]
    public async Task OneSweep_RemovesNoMoreThanItsCap()
    {
        var runs = new List<string>();
        for (var i = 0; i < 3; i++)
            runs.Add(await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid())));
        runs.ForEach(run => Age(run, Old));

        var deleted = await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 2, CancellationToken.None);

        deleted.Should().Be(2);
        runs.Count(Directory.Exists).Should().Be(1);
    }

    [SkippableFact]
    public async Task ALinkInsideAnExpiredRun_IsNotFollowed()
    {
        var run = await WriteRunAsync(TraceScope.ForExecution(Guid.NewGuid()));
        Directory.CreateDirectory(Path.Combine(_outside, "turns", "1"));
        var target = Path.Combine(_outside, "turns", "1", "model_response.md");
        await File.WriteAllTextAsync(target, "not a trace");
        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(run, "linked"), _outside);
        Age(run, Old);

        await _sut.DeleteRunsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        File.Exists(target).Should().BeTrue("a file reached through a link is not the store's to delete");
        File.Exists(Path.Combine(run, "manifest.json")).Should().BeFalse();
        Directory.Exists(run).Should().BeTrue("a folder still holding a link is not emptied of it");
    }

    [Theory]
    [InlineData(true, 30, 30)]
    [InlineData(false, 30, null)]
    [InlineData(true, 0.5, null)]
    public void TheReceiptPeriod_IsReportedOnlyWhileTheSweepRuns(bool enabled, double days, int? expectedDays)
    {
        _config.MetaHarness.TraceRetention.Enabled = enabled;
        _config.MetaHarness.TraceRetention.RetainFor = TimeSpan.FromDays(days);

        _sut.CurrentRetentionPeriod.Should().Be(expectedDays is { } d ? TimeSpan.FromDays(d) : null);
    }

    private async Task<string> WriteRunAsync(TraceScope scope)
    {
        await using var writer = await _sut.StartRunAsync(
            scope, new RunMetadata { AgentName = "agent", StartedAt = Now });
        await writer.WriteTurnAsync(1, new TurnArtifacts { TurnNumber = 1, SystemPrompt = "prompt", ModelResponse = "answer" });
        await writer.AppendTraceAsync(new ExecutionTraceRecord { Type = "tool_call", ExecutionRunId = scope.ExecutionRunId.ToString("D") });
        return writer.RunDirectory;
    }

    private static void Age(string directory, DateTime when)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, when);
        foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).Append(directory))
            Directory.SetLastWriteTimeUtc(dir, when);
    }
}
