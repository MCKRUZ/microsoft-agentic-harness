using System.Text.Json;
using Domain.Common.Compliance;
using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Context;
using Infrastructure.AI.Telemetry.Redaction;
using Infrastructure.AI.Tests.Planner.StepExecutors;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Context;

/// <summary>
/// Personal-data export (#902) for <see cref="FileSystemToolResultStore"/>: the copy holds the spills of the person's
/// conversations and planned tasks, and nothing of anyone else's.
/// </summary>
/// <remarks>
/// The store records no owner; a spill is the person's only through the ids that lead to it. So the property that
/// matters is that export reads exactly the directories erasure deletes: a copy smaller than the erasure leaves the person
/// unaware of data the system holds, and a larger one discloses someone else's.
/// </remarks>
public sealed class FileSystemToolResultStoreExportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _root;
    private readonly FileSystemToolResultStore _sut;

    public FileSystemToolResultStoreExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-export-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(_root);

        var appConfig = new AppConfig();
        appConfig.AI.ContextManagement.ToolResultStorage = new ToolResultStorageConfig
        {
            PerResultCharLimit = 100,
            PreviewSizeChars = 20,
            StoragePath = _root,
        };
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        _sut = new FileSystemToolResultStore(
            monitor.Object,
            PermissiveAdmission.PermissiveSanitizer(),
            new DefaultContentRedactionFilter(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<FileSystemToolResultStore>>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ExportSubjectAsync_ConversationsAndPlannedTasks_ReturnsTheirSpillsWithTheIdTheyWereWrittenUnder()
    {
        await SpillAsync("conv-a", "own conversation");
        await SpillAsync("plan-1:step-a", "model step");
        await SpillAsync("job-1", "tool step");
        await SpillAsync("conv-b", "someone else");
        await SpillAsync("job-2", "another run");

        var records = await _sut.ExportSubjectAsync(
            new ExportSubject("user-a", ["conv-a"], new PlanRunTraces(["plan-1:step-a"], ["job-1"], ["planrun:plan-1"])),
            CancellationToken.None);

        records.Select(r => r.GetProperty("scope").GetString()).Should().BeEquivalentTo(
            ["conv-a", "plan-1:step-a", "job-1"],
            "a step's directory is stored with '~' for ':', but the copy names the id the person's run used");
        Text(records).Should().Contain("own conversation").And.Contain("model step").And.Contain("tool step");
        Text(records).Should().NotContain("someone else").And.NotContain("another run");
        records.Should().AllSatisfy(r => r.GetProperty("resultId").GetString().Should().NotBeNullOrEmpty());
    }

    [Fact]
    public async Task ExportSubjectAsync_HoldsExactlyWhatErasureThenDeletes()
    {
        await SpillAsync("conv-a", "one");
        await SpillAsync("conv-a", "two");
        await SpillAsync("plan-1:step-a", "three");
        await SpillAsync("job-1", "four");
        await SpillAsync("conv-b", "kept");
        var traces = new PlanRunTraces(["plan-1:step-a"], ["job-1"], []);

        var exported = await _sut.ExportSubjectAsync(new ExportSubject("user-a", ["conv-a"], traces), CancellationToken.None);
        var erased = await _sut.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-a"], traces), CancellationToken.None);

        exported.Should().HaveCount(erased, "the copy must hold what an erasure would delete, no more and no less");
        exported.Should().HaveCount(4);
    }

    [Fact]
    public async Task ExportSubjectAsync_PathShapedIds_AreSkippedAndNothingOutsideTheRootIsRead()
    {
        await SpillAsync("conv-a", "mine");
        Plant(Path.Combine(_tempDir, "tool-results"));
        Plant(Path.Combine(_tempDir, "x", "tool-results"));
        Plant(Path.Combine(_root, "a", "b", "tool-results"));

        var records = await _sut.ExportSubjectAsync(
            new ExportSubject("user-a", ["..", "../x", "conv-a", "a/b", @"..\x"]), CancellationToken.None);

        records.Should().ContainSingle();
        Text(records).Should().NotContain("outside the root");
    }

    [Fact]
    public async Task ExportSubjectAsync_NothingSpilled_ReturnsNothing()
    {
        await SpillAsync("conv-b", "someone else");

        (await _sut.ExportSubjectAsync(new ExportSubject("user-a", ["conv-a"]), CancellationToken.None)).Should().BeEmpty();
    }

    private async Task SpillAsync(string scopeId, string marker)
    {
        var content = marker + string.Concat(Enumerable.Repeat(" and the search returned this row", 6));
        (await _sut.StoreIfLargeAsync(scopeId, "search", null, content, scopeIsRetrievable: true))
            .IsPersistedToDisk.Should().BeTrue("the seed must actually spill");
    }

    private static string Text(IEnumerable<JsonElement> records) => string.Join("\n", records.Select(r => r.GetRawText()));

    private static void Plant(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sentinel.txt"), "outside the root");
    }
}
