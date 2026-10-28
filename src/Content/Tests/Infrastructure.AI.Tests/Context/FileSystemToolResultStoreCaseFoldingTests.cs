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
/// Spill folders of ids that differ only by case (#928, #936). The conversation stores compare ids exactly, so
/// <c>ticket-42</c> and <c>TICKET-42</c> are two conversations with two owners; a folder name that keeps the id's case is
/// one folder on Windows and default macOS, and then the two people's spills mix, and erasing, exporting or expiring
/// either one reaches the other's.
/// </summary>
/// <remarks>
/// CI runs on Linux, whose file system keeps the two names apart, so a test that only watches what the OS does would
/// pass there for the wrong reason. The property is asserted on the names themselves instead: two ids must map to folder
/// names that differ even when compared ignoring case, which is what a case-insensitive file system compares.
/// </remarks>
public sealed class FileSystemToolResultStoreCaseFoldingTests : IDisposable
{
    private static readonly string LargeOutput = new('x', 200);

    private readonly string _tempDir;
    private readonly string _root;
    private readonly FileSystemToolResultStore _sut;

    public FileSystemToolResultStoreCaseFoldingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-case-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("ticket-42", "TICKET-42")]
    [InlineData("conv-a", "Conv-A")]
    [InlineData("plan-1:step-a", "PLAN-1:step-a")]
    public async Task StoreIfLargeAsync_IdsDifferingOnlyByCase_GetFolderNamesThatDifferIgnoringCase(string first, string second)
    {
        var a = await SpillAsync(first);
        var b = await SpillAsync(second);

        string.Equals(ScopeFolderName(a), ScopeFolderName(b), StringComparison.OrdinalIgnoreCase).Should().BeFalse(
            "a case-insensitive file system would resolve both names to one folder, mixing two people's spills");
    }

    [Fact]
    public async Task EraseSubjectAsync_CaseVariantOfAnotherPersonsId_LeavesTheirSpill()
    {
        var victim = await SpillAsync("ticket-42");
        await SpillAsync("TICKET-42");

        var removed = await _sut.EraseSubjectAsync(new ErasureSubject("attacker", ["TICKET-42"]), CancellationToken.None);

        removed.Should().Be(1, "only the erased person's own spill goes");
        File.Exists(victim.FullContentPath!).Should().BeTrue("another person's spill under a case variant must survive");
    }

    [Fact]
    public async Task ExportSubjectAsync_CaseVariantOfAnotherPersonsId_HoldsOnlyTheirOwnSpill()
    {
        await SpillAsync("ticket-42");
        await SpillAsync("TICKET-42");

        var records = await _sut.ExportSubjectAsync(new ExportSubject("attacker", ["TICKET-42"]), CancellationToken.None);

        records.Should().ContainSingle("another person's spill under a case variant must not be disclosed");
    }

    [Fact]
    public async Task RetrievePageAsync_CaseVariantScope_DoesNotReachTheOtherIdsSpill()
    {
        var victim = await SpillAsync("ticket-42");

        var act = () => _sut.RetrievePageAsync(victim.ResultId, "TICKET-42", 0, 50);

        await act.Should().ThrowAsync<KeyNotFoundException>("a case variant is a different scope");
    }

    [Fact]
    public async Task StoreIfLargeAsync_LongestAllowedUpperCaseId_StillSpills()
    {
        // The allowlist admits 200 characters; a folder name may hold at most 255 on NTFS and ext4.
        var id = new string('A', 200);

        var reference = await SpillAsync(id);

        ScopeFolderName(reference).Length.Should().BeLessThanOrEqualTo(255);
        (await _sut.RetrievePageAsync(reference.ResultId, id, 0, 50)).Text.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("ticket-42")]
    [InlineData("TICKET-42")]
    [InlineData("plan-1:step-a")]
    [InlineData("C:")]
    public async Task StoreIfLargeAsync_AnyId_FolderNameIsLowerCaseAndLegalOnEveryFileSystem(string id)
    {
        if (OperatingSystem.IsWindows() && id == "C:")
            return; // Drive-rooted there, and refused before a name is made.

        var name = ScopeFolderName(await SpillAsync(id));

        name.Should().Be(name.ToLowerInvariant(), "a lower-case name compares the same with and without case");
        name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).Should().Be(-1, "Windows refuses these in a name");
        name.Should().NotEndWith(".", "Windows strips a trailing dot");
    }

    [Fact]
    public async Task StoreIfLargeAsync_SameId_AlwaysGetsTheSameFolder()
    {
        var first = await SpillAsync("Ticket-42");
        var second = await SpillAsync("Ticket-42");

        ScopeFolderName(first).Should().Be(ScopeFolderName(second), "a later page fetch or erasure must find the folder again");
    }

    [Fact]
    public async Task EraseSubjectAsync_FolderWrittenUnderTheOldName_IsRemoved()
    {
        // Written before #928, when the folder was named after the id with only ':' replaced.
        var legacy = PlantLegacySpill("Ticket-42");
        var legacyStep = PlantLegacySpill("plan-1~step-a");

        var removed = await _sut.EraseSubjectAsync(
            new ErasureSubject("user-a", ["Ticket-42"], new PlanRunTraces(["plan-1:step-a"], [], [])),
            CancellationToken.None);

        removed.Should().Be(2);
        File.Exists(legacy).Should().BeFalse("erasure must still find spills written under the old name");
        File.Exists(legacyStep).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "Ticket-42")).Should().BeFalse("the emptied old folder goes too");
    }

    [Fact]
    public async Task ExportSubjectAsync_FolderWrittenUnderTheOldName_IsIncluded()
    {
        PlantLegacySpill("Ticket-42");

        var records = await _sut.ExportSubjectAsync(new ExportSubject("user-a", ["Ticket-42"]), CancellationToken.None);

        records.Should().ContainSingle("the copy holds what erasure deletes, old folders included")
            .Which.GetProperty("scope").GetString().Should().Be("Ticket-42");
    }

    [Fact]
    public async Task EraseSubjectAsync_OldFolderNamedForACaseVariant_IsNotTreatedAsThisIds()
    {
        // On a case-insensitive file system "ticket-42" is also where "TICKET-42" would resolve; only a folder whose
        // stored name is exactly this id's old name was created for it.
        var other = PlantLegacySpill("ticket-42");

        var removed = await _sut.EraseSubjectAsync(new ErasureSubject("attacker", ["TICKET-42"]), CancellationToken.None);

        removed.Should().Be(0);
        File.Exists(other).Should().BeTrue("an old folder created for another id must not be erased for a case variant");
    }

    [Fact]
    public async Task ExportSubjectAsync_OldFolderNamedForACaseVariant_IsNotInTheCopy()
    {
        PlantLegacySpill("ticket-42");

        var records = await _sut.ExportSubjectAsync(new ExportSubject("attacker", ["TICKET-42"]), CancellationToken.None);

        records.Should().BeEmpty("an old folder created for another id must not be disclosed to a case variant");
    }

    [Fact]
    public async Task RetrievePageAsync_ResultWrittenUnderTheOldName_IsNotFound()
    {
        // Reads do not fall back to the old name: on a case-insensitive file system that folder may hold another id's
        // spills. A reference from before the upgrade is lost; the retention sweep removes the file.
        var resultId = Guid.NewGuid().ToString("N");
        PlantLegacySpill("ticket-42", resultId);

        var act = () => _sut.RetrievePageAsync(resultId, "ticket-42", 0, 50);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task PruneExpiredAsync_FolderWrittenUnderTheOldName_IsSwept()
    {
        var legacy = PlantLegacySpill("Ticket-42");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow - TimeSpan.FromDays(8));

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(7));

        removed.Should().Be(1);
        File.Exists(legacy).Should().BeFalse("old folders get no new writes and drain through retention");
    }

    private async Task<Domain.AI.Context.ToolResultReference> SpillAsync(string scopeId)
    {
        var reference = await _sut.StoreIfLargeAsync(scopeId, "search", null, LargeOutput, scopeIsRetrievable: true);
        reference.IsPersistedToDisk.Should().BeTrue("the seed must actually spill");
        return reference;
    }

    /// <summary>The scope folder a spill landed in: <c>{root}/{scope}/tool-results/{resultId}.txt</c>.</summary>
    private static string ScopeFolderName(Domain.AI.Context.ToolResultReference reference) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(reference.FullContentPath!))!);

    private string PlantLegacySpill(string legacyFolderName, string? resultId = null)
    {
        var directory = Path.Combine(_root, legacyFolderName, "tool-results");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{resultId ?? Guid.NewGuid().ToString("N")}.txt");
        File.WriteAllText(file, "written before #928");
        return file;
    }
}
