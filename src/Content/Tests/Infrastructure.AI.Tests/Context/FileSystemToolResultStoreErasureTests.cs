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
/// Right-to-erasure (#900) for <see cref="FileSystemToolResultStore"/>: the spilled tool results of the person's
/// conversations are deleted from disk and from the page cache, and nothing else — another conversation's spills, or
/// any file outside the storage root — is touched.
/// </summary>
/// <remarks>
/// Over-deletion destroys another person's data; under-deletion leaves the person's data behind while the erasure
/// receipt claims success. Conversation ids become directory names here, so an id shaped like a path is the
/// over-deletion risk specific to this store: it must be skipped, never resolved to a directory outside the root.
/// </remarks>
public sealed class FileSystemToolResultStoreErasureTests : IDisposable
{
    private static readonly string LargeOutput = new('x', 200);

    private readonly string _tempDir;
    private readonly string _root;
    private readonly FileSystemToolResultStore _sut;

    public FileSystemToolResultStoreErasureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-erase-" + Guid.NewGuid().ToString("N"));
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
    public async Task EraseSubjectAsync_SubjectsConversations_RemovesOnlyTheirSpills()
    {
        await SpillAsync("conv-a");
        await SpillAsync("conv-a");
        await SpillAsync("run:step-1");
        await SpillAsync("conv-b");
        SpillFiles("conv-a").Should().HaveCount(2, "the seed must exist before erasing");
        SpillFiles("run~step-1").Should().ContainSingle("a colon is stored as '~'");

        var removed = await _sut.EraseSubjectAsync(
            new ErasureSubject("user-a", ["conv-a", "run:step-1", "conv-never-spilled"]), CancellationToken.None);

        removed.Should().Be(3);
        Directory.Exists(Path.Combine(_root, "conv-a")).Should().BeFalse("an emptied conversation directory goes too");
        Directory.Exists(Path.Combine(_root, "run~step-1")).Should().BeFalse();
        SpillFiles("conv-b").Should().ContainSingle("another conversation's spill must survive");
    }

    [Fact]
    public async Task EraseSubjectAsync_PathShapedConversationIds_AreSkippedAndNothingOutsideTheRootIsTouched()
    {
        await SpillAsync("conv-a");
        await SpillAsync("conv-b");
        // Where each refused id would land if it were ever combined into a path.
        var parentSentinel = Plant(Path.Combine(_tempDir, "tool-results"));            // ".."
        var siblingSentinel = Plant(Path.Combine(_tempDir, "x", "tool-results"));      // "../x"
        var nestedSentinel = Plant(Path.Combine(_root, "a", "b", "tool-results"));     // "a/b"

        var removed = await _sut.EraseSubjectAsync(
            new ErasureSubject("user-a", ["..", "../x", "conv-a", "a/b", @"..\x"]), CancellationToken.None);

        removed.Should().Be(1, "only the one valid conversation's spill is removed");
        File.Exists(parentSentinel).Should().BeTrue("'..' must never resolve to the storage root's parent");
        File.Exists(siblingSentinel).Should().BeTrue("'../x' must never reach a sibling of the storage root");
        File.Exists(nestedSentinel).Should().BeTrue("'a/b' is not a conversation directory");
        SpillFiles("conv-a").Should().BeEmpty();
        SpillFiles("conv-b").Should().ContainSingle();
    }

    [Fact]
    public async Task EraseSubjectAsync_CachedPage_IsNoLongerServedAfterErasure()
    {
        var reference = await SpillAsync("conv-a");
        var before = await _sut.RetrievePageAsync(reference.ResultId, "conv-a", 0, 50);
        before.Text.Should().NotBeEmpty("reading once puts the page in the cache");

        await _sut.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-a"]), CancellationToken.None);

        var act = () => _sut.RetrievePageAsync(reference.ResultId, "conv-a", 0, 50);
        await act.Should().ThrowAsync<KeyNotFoundException>("an erased spill must not keep being served from the cache");
    }

    [Fact]
    public async Task EraseSubjectAsync_NothingSpilled_ReturnsZeroAndChangesNothing()
    {
        await SpillAsync("conv-b");

        var removed = await _sut.EraseSubjectAsync(new ErasureSubject("user-a", ["conv-a"]), CancellationToken.None);

        removed.Should().Be(0);
        SpillFiles("conv-b").Should().ContainSingle();
    }

    private async Task<Domain.AI.Context.ToolResultReference> SpillAsync(string conversationId)
    {
        var reference = await _sut.StoreIfLargeAsync(conversationId, "search", null, LargeOutput, scopeIsRetrievable: true);
        reference.IsPersistedToDisk.Should().BeTrue("the seed must actually spill");
        return reference;
    }

    private string[] SpillFiles(string directoryName)
    {
        var directory = Path.Combine(_root, directoryName, "tool-results");
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    private static string Plant(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "sentinel.txt");
        File.WriteAllText(file, "must survive");
        return file;
    }
}
