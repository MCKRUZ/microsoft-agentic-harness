using Domain.Common.Config.Infrastructure;
using FluentAssertions;
using Infrastructure.AI.Generators;
using Infrastructure.AI.StateManagement;
using Infrastructure.AI.StateManagement.Checkpoints;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.StateManagement;

/// <summary>
/// A workflow's saved state (the JSON checkpoint and its markdown rendering) ages out by its newest file (#901), and the
/// sweep reaches only folders whose state names them: a base path shared with other content keeps that content.
/// </summary>
public sealed class JsonCheckpointStateManagerRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddDays(-90);
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-120);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"workflow-retention-{Guid.NewGuid():N}");
    private readonly InfrastructureConfig _config = new();
    private readonly CompositeStateManager _composite;

    public JsonCheckpointStateManagerRetentionTests()
    {
        _config.StateManagement.BasePath = _root;
        var monitor = new Mock<IOptionsMonitor<InfrastructureConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        _composite = new CompositeStateManager(
            NullLogger<CompositeStateManager>.Instance, new StateMarkdownGenerator(), monitor.Object);
    }

    private JsonCheckpointStateManager Checkpoints => _composite.Checkpoints!;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task AWorkflowNothingHasSavedSinceTheCutoff_LosesItsStateAndRendering_AndARecentOneIsKept()
    {
        await SaveNewAsync("old-workflow");
        await SaveNewAsync("recent-workflow");
        Age("old-workflow", Old);

        var deleted = await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        deleted.Should().Be(1);
        Directory.Exists(Path.Combine(_root, "old-workflow")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "recent-workflow", "checkpoints", "workflow-state.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "recent-workflow", "inputs", "workflow-state.md")).Should().BeTrue();
    }

    [Fact]
    public async Task AnythingElseInTheWorkflowsFolder_Survives()
    {
        await SaveNewAsync("with-inputs");
        var input = Path.Combine(_root, "with-inputs", "inputs", "brief.md");
        await File.WriteAllTextAsync(input, "an input someone placed here");
        Age("with-inputs", Old);

        await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None);

        File.Exists(input).Should().BeTrue();
        File.Exists(Path.Combine(_root, "with-inputs", "checkpoints", "workflow-state.json")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "with-inputs", "inputs", "workflow-state.md")).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"workflowId":"some-other-workflow"}""")]
    [InlineData("not json at all")]
    public async Task AFolderWhoseStateDoesNotNameIt_IsNotAWorkflow(string content)
    {
        // "projects" is a generic folder name; a file merely called workflow-state.json is not proof this store wrote it.
        var stateFile = Path.Combine(_root, "foreign", "checkpoints", "workflow-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        await File.WriteAllTextAsync(stateFile, content);
        Age("foreign", Old);

        (await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None)).Should().Be(0);
        File.Exists(stateFile).Should().BeTrue();
    }

    [SkippableFact]
    public async Task AWorkflowThatCouldNotBeFullyDeleted_KeepsItsState_AndTheNextSweepFinishesIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Deleting an open file only fails on Windows.");
        await SaveNewAsync("locked");
        Age("locked", Old);
        var rendering = Path.Combine(_root, "locked", "inputs", "workflow-state.md");

        using (new FileStream(rendering, FileMode.Open, FileAccess.Read, FileShare.None))
            (await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None)).Should().Be(0);

        File.Exists(Path.Combine(_root, "locked", "checkpoints", "workflow-state.json")).Should().BeTrue();
        (await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 100, CancellationToken.None)).Should().Be(1);
        Directory.Exists(Path.Combine(_root, "locked")).Should().BeFalse();
    }

    [Fact]
    public async Task OneSweep_RemovesNoMoreThanItsCap()
    {
        foreach (var id in new[] { "w1", "w2", "w3" })
        {
            await SaveNewAsync(id);
            Age(id, Old);
        }

        (await Checkpoints.DeleteWorkflowsNotWrittenSinceAsync(Cutoff, 2, CancellationToken.None)).Should().Be(2);
    }

    [Theory]
    [InlineData(true, 90, 90)]
    [InlineData(false, 90, null)]
    [InlineData(true, 0.5, null)]
    public void TheReceiptPeriod_IsReportedOnlyWhileTheSweepRuns_ForTheStateAndItsRendering(
        bool enabled, double days, int? expectedDays)
    {
        _config.StateManagement.Retention.Enabled = enabled;
        _config.StateManagement.Retention.RetainFor = TimeSpan.FromDays(days);
        var expected = expectedDays is { } d ? TimeSpan.FromDays(d) : (TimeSpan?)null;

        Checkpoints.CurrentRetentionPeriod.Should().Be(expected);
        _composite.Markdown!.CurrentRetentionPeriod.Should().Be(expected);
    }

    /// <summary>Creates a workflow and saves it once, which is when the markdown rendering is written.</summary>
    private async Task SaveNewAsync(string workflowId) =>
        await _composite.SaveAsync(await _composite.CreateAsync(workflowId));

    private void Age(string workflowId, DateTime when)
    {
        var folder = Path.Combine(_root, workflowId);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, when);
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
            Directory.SetLastWriteTimeUtc(dir, when);
    }
}
