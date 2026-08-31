using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Context;

public sealed class FileSystemToolResultStoreTests : IDisposable
{
    private const int LargePageSize = 10_000;

    private readonly FileSystemToolResultStore _sut;
    private readonly AppConfig _appConfig;
    private readonly string _tempDir;

    public FileSystemToolResultStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _appConfig = new AppConfig();
        _appConfig.AI.ContextManagement.ToolResultStorage = new ToolResultStorageConfig
        {
            PerResultCharLimit = 100,
            PreviewSizeChars = 20,
            StoragePath = _tempDir
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);

        _sut = new FileSystemToolResultStore(
            monitor.Object,
            Mock.Of<ILogger<FileSystemToolResultStore>>());
    }

    [Fact]
    public async Task StoreIfLargeAsync_SmallResult_ReturnsInlineWithFullContent()
    {
        var output = "small output";

        var result = await _sut.StoreIfLargeAsync("session1", "read_file", null, output);

        result.PreviewContent.Should().Be(output);
        result.FullContentPath.Should().BeNull();
        result.IsPersistedToDisk.Should().BeFalse();
        result.SizeChars.Should().Be(output.Length);
    }

    [Fact]
    public async Task StoreIfLargeAsync_LargeResult_PersistsToDiskWithPreview()
    {
        var output = new string('x', 200);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output);

        result.IsPersistedToDisk.Should().BeTrue();
        result.FullContentPath.Should().NotBeNullOrWhiteSpace();
        result.SizeChars.Should().Be(200);
        result.PreviewContent.Should().StartWith(new string('x', 20));
    }

    [Fact]
    public async Task StoreIfLargeAsync_OutputLargerThanMaxSpillChars_TruncatesAtTheCap()
    {
        // #563: MaxSpillChars bounds disk, independent of PerResultCharLimit — an unbounded write
        // here would be the same "no silent caps" gap this cap exists to close, one layer down.
        _appConfig.AI.ContextManagement.ToolResultStorage.MaxSpillChars = 150;
        var output = new string('x', 500);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output);

        result.SizeChars.Should().Be(150);
        var page = await _sut.RetrievePageAsync(result.ResultId, "session1", offset: 0, LargePageSize);
        page.Text.Should().Be(new string('x', 150));
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task StoreAndRetrieve_RoundTrips()
    {
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);

        page.Text.Should().Be(output);
        page.HasMore.Should().BeFalse();
        page.TotalChars.Should().Be(output.Length);
    }

    [Fact]
    public async Task RetrievePageAsync_MissingId_ThrowsKeyNotFoundException()
    {
        var act = () => _sut.RetrievePageAsync("nonexistent-id", "session1", offset: 0, LargePageSize);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RetrievePageAsync_WrongScope_ThrowsKeyNotFoundException()
    {
        // #521: the retrieval scope must match the write scope. A different (but otherwise valid)
        // scopeId must be refused identically to a resultId that was never stored at all — see
        // IToolResultStore.RetrievePageAsync's own remarks for why the two must be indistinguishable
        // from outside the store.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var act = () => _sut.RetrievePageAsync(stored.ResultId, "session2", offset: 0, LargePageSize);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RetrievePageAsync_AfterANewStoreInstance_StillFindsTheResult()
    {
        // #521: the path is reconstructed deterministically from (scopeId, resultId), not trusted from
        // an in-memory index — proving this needs a genuinely SEPARATE store instance (a fresh process
        // would build its own empty index), not just a second call on the same _sut.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var freshInstance = new FileSystemToolResultStore(monitor.Object, Mock.Of<ILogger<FileSystemToolResultStore>>());

        var page = await freshInstance.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);

        page.Text.Should().Be(output);
    }

    [Theory]
    // Reaches ANOTHER scope's directory: Path.Combine does not normalize, the file APIs do.
    [InlineData("../../session1/tool-results/PLACEHOLDER")]
    [InlineData("..\\..\\session1\\tool-results\\PLACEHOLDER")]
    // A ROOTED segment makes Path.Combine discard every earlier segment — arbitrary *.json read.
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\attacker\\share\\payload")]
    // Not a traversal, just not an id this store ever mints — refused for the same reason.
    [InlineData("nonexistent-id")]
    public async Task RetrievePageAsync_ResultIdThatIsNotAMintedId_ThrowsKeyNotFoundException(string resultId)
    {
        // resultId is model-supplied (ToolResultFetchTool hands the LLM's own argument straight to this
        // method), so an unsanitized one is a path-traversal sink: sanitizing scopeId alone leaves the
        // isolation boundary reachable by writing the traversal into the OTHER path segment instead.
        // Mutation test: delete the Guid.TryParseExact guard in RetrievePageAsync and the first two
        // cases retrieve session1's data from session2's scope.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var act = () => _sut.RetrievePageAsync(
            resultId.Replace("PLACEHOLDER", stored.ResultId, StringComparison.Ordinal),
            "session2", offset: 0, LargePageSize);

        // KeyNotFoundException, not ArgumentException: a caller must not learn from the exception type
        // which of its guesses was better-formed — see the interface's remarks on why "exists but not
        // yours" and "never existed" must be indistinguishable.
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task StoreIfLargeAsync_PathTraversalInSessionId_ThrowsArgumentException()
    {
        var act = () => _sut.StoreIfLargeAsync("../escape", "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Theory]
    [InlineData("session1 ")]
    [InlineData("session1.")]
    public async Task StoreIfLargeAsync_TrailingDotOrSpaceInSessionId_ThrowsArgumentException(string sessionId)
    {
        // Security-review finding: Windows silently trims trailing dots/spaces off a path segment, so
        // "session1" and "session1 " would resolve to the SAME directory there even though they compare
        // unequal as strings — two different scopes colliding onto one storage directory. Rejected here
        // rather than allowed to (on Windows only) collide with a different caller's scope.
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Theory]
    // #560: the allowlist closes this for every producer of a scope id, not just the two paths a
    // prior review happened to find — a caller-controlled conversation id or run id with any of
    // these shapes must be refused before it ever becomes a directory name.
    [InlineData("has space")]
    [InlineData("percent%20encoded")]
    [InlineData("semi;colon")]
    [InlineData("null\0byte")]
    [InlineData("emoji🙂")]
    public async Task StoreIfLargeAsync_SessionIdOutsideTheAllowedCharset_ThrowsArgumentException(string sessionId)
    {
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Theory]
    // Negative controls: every character class the allowlist admits, proving the regex above isn't
    // accidentally rejecting a legitimate id shape while it's busy rejecting the bad ones.
    [InlineData("conversation-id-123")]
    [InlineData("plan_run.42")]
    [InlineData("scope:with:colons")]
    [InlineData("ABCDEFabcdef0123456789")]
    public async Task StoreIfLargeAsync_SessionIdWithinTheAllowedCharset_DoesNotThrow(string sessionId)
    {
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, "data");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RetrievePageAsync_TrailingSpaceInScopeId_ThrowsArgumentExceptionNamingScopeId()
    {
        // Same collision guard, exercised through the retrieval side — and confirms the exception names
        // the CALLING method's own parameter (scopeId), not a copy-pasted "sessionId" from the sibling
        // method that shares this validation helper (a /code-review finding).
        var act = () => _sut.RetrievePageAsync(Guid.NewGuid().ToString("N"), "session1 ", offset: 0, LargePageSize);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("scopeId");
    }

    [Fact]
    public async Task StoreIfLargeAsync_NullOutput_ThrowsArgumentNullException()
    {
        var act = () => _sut.StoreIfLargeAsync("session1", "tool", null, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StoreIfLargeAsync_EmptySessionId_ThrowsArgumentException()
    {
        var act = () => _sut.StoreIfLargeAsync("", "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- Pagination (#563) -----------------------------------------------------------------

    [Fact]
    public async Task RetrievePageAsync_OffsetBeyondTheEnd_ReturnsEmptyWithHasMoreFalse()
    {
        // Must exceed this fixture's 100-char PerResultCharLimit or StoreIfLargeAsync keeps it inline
        // and there is no file for RetrievePageAsync to read.
        var output = new string('a', 150);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 1_000, maxChars: 10);

        page.Text.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.TotalChars.Should().Be(150);
    }

    [Fact]
    public async Task RetrievePageAsync_WalkingEveryPage_ReassemblesTextIdenticalToWhatWasStored()
    {
        var output = string.Concat(Enumerable.Range(0, 500).Select(i => (char)('a' + i % 26)));
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var reassembled = "";
        var offset = 0;
        var pageCount = 0;
        while (true)
        {
            var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset, maxChars: 37);
            reassembled += page.Text;
            pageCount++;
            if (!page.HasMore) break;
            offset = page.NextOffset;
        }

        reassembled.Should().Be(output);
        pageCount.Should().BeGreaterThan(1, "the test is meaningless if one page already covered everything");
    }

    [Fact]
    public async Task RetrievePageAsync_MultiByteCharacterOnAPageBoundary_IsNotSplit()
    {
        // U+1F642 (🙂) is a UTF-16 surrogate pair, placed so a naive char-count cut at maxChars=91
        // would land exactly between the high and low surrogate. Padded past this fixture's 100-char
        // PerResultCharLimit or StoreIfLargeAsync keeps it inline and there is no file to page-read.
        var output = new string('a', 90) + "\U0001F642" + new string('b', 10);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var first = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 91);
        var second = await _sut.RetrievePageAsync(stored.ResultId, "session1", first.NextOffset, maxChars: 100);

        (first.Text + second.Text).Should().Be(output);
        first.Text.Should().NotEndWith("\uD83D"); // lone high surrogate would prove the pair was split
    }

    // --- Retention sweep (#559) -------------------------------------------------------------

    [Fact]
    public async Task PruneExpiredAsync_FileOlderThanGracePeriod_IsDeleted()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200));
        File.SetLastWriteTimeUtc(stored.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(1);
        File.Exists(stored.FullContentPath!).Should().BeFalse();
    }

    [Fact]
    public async Task PruneExpiredAsync_FileWithinGracePeriod_IsKept()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200));

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(0, "the file was just written — well within a one-day grace period");
        File.Exists(stored.FullContentPath!).Should().BeTrue();
    }

    [Fact]
    public async Task PruneExpiredAsync_RemovesTheNowEmptyScopeAndToolResultsDirectories()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200));
        File.SetLastWriteTimeUtc(stored.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));
        var toolResultsDir = Path.GetDirectoryName(stored.FullContentPath!)!;
        var scopeDir = Path.GetDirectoryName(toolResultsDir)!;

        await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        Directory.Exists(toolResultsDir).Should().BeFalse(
            "a fully swept scope must not leave an empty tool-results directory behind forever");
        Directory.Exists(scopeDir).Should().BeFalse();
    }

    [Fact]
    public async Task PruneExpiredAsync_MixOfOldAndFreshFiles_KeepsOnlyTheFreshOnes()
    {
        var stale = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200));
        File.SetLastWriteTimeUtc(stale.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));
        var fresh = await _sut.StoreIfLargeAsync("session2", "tool", null, new string('b', 200));

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(1);
        File.Exists(stale.FullContentPath!).Should().BeFalse();
        File.Exists(fresh.FullContentPath!).Should().BeTrue();
    }

    [Fact]
    public async Task PruneExpiredAsync_StoragePathDoesNotExist_ReturnsZeroWithoutThrowing()
    {
        Directory.Delete(_tempDir, recursive: true);

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(0);
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
            // Best-effort cleanup in test
        }
    }
}
