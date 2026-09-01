using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Context;
using Microsoft.Extensions.Caching.Memory;
using Infrastructure.AI.Telemetry.Redaction;
using Infrastructure.AI.Tests.Planner.StepExecutors;
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
            PermissiveAdmission.PermissiveSanitizer(),
            new DefaultContentRedactionFilter(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<ILogger<FileSystemToolResultStore>>());
    }

    [Fact]
    public async Task StoreIfLargeAsync_SmallResult_ReturnsInlineWithFullContent()
    {
        var output = "small output";

        var result = await _sut.StoreIfLargeAsync("session1", "read_file", null, output, scopeIsRetrievable: true);

        result.PreviewContent.Should().Be(output);
        result.FullContentPath.Should().BeNull();
        result.IsPersistedToDisk.Should().BeFalse();
        result.SizeChars.Should().Be(output.Length);
    }

    [Fact]
    public async Task StoreIfLargeAsync_LargeResult_PersistsToDiskWithPreview()
    {
        var output = new string('x', 200);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output, scopeIsRetrievable: true);

        result.IsPersistedToDisk.Should().BeTrue();
        result.FullContentPath.Should().NotBeNullOrWhiteSpace();
        result.SizeChars.Should().Be(200);
        result.PreviewContent.Should().StartWith(new string('x', 20));
    }

    [Fact]
    public async Task StoreIfLargeAsync_ScopeNotRetrievable_WritesNoFileAndReturnsThePlainReferenceRegardlessOfSize()
    {
        // #575: the single chokepoint for "can a spilled file here ever be fetched back" — previously
        // enforced independently by each of the two production callers before this method was even
        // invoked. Content well over PerResultCharLimit (100) still must not spill when the scope can
        // never be fetched back: writing a file nobody can ever retrieve is pure disk growth.
        var output = new string('x', 200);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output, scopeIsRetrievable: false);

        result.FullContentPath.Should().BeNull();
        result.IsPersistedToDisk.Should().BeFalse();
        result.PreviewContent.Should().Be(output);
        result.SizeChars.Should().Be(output.Length);
        Directory.EnumerateFiles(_tempDir, "*.txt", SearchOption.AllDirectories).Should().BeEmpty(
            "no file may exist on disk that nothing can ever be asked to fetch back");
    }

    [Fact]
    public async Task RetrievePageAsync_WalkingEveryPage_ReadsTheUnderlyingFileOnceNotOncePerPage()
    {
        // #574: RetrievePageAsync used to call File.ReadAllTextAsync on every single page fetch — an
        // O(fileSize) read repeated once per page. A short-lived IMemoryCache now serves every page in
        // one fetch sequence from a single disk read. Proven here by pointing the store's page cache at
        // a spy IMemoryCache that counts real GetOrCreate/factory invocations rather than by timing —
        // timing a two-page walk is not a reliable signal on a shared CI runner.
        var countingCache = new CountingMemoryCache();
        var sut = new FileSystemToolResultStore(
            OptionsMonitor(),
            PermissiveAdmission.PermissiveSanitizer(),
            new DefaultContentRedactionFilter(),
            countingCache,
            Mock.Of<ILogger<FileSystemToolResultStore>>());

        // sizeThreshold forces a spill despite the shared fixture's 100-char PerResultCharLimit — this
        // test is about page-fetch caching, not the inline/spill boundary.
        var output = new string('x', 60);
        var stored = await sut.StoreIfLargeAsync(
            "session1", "search", null, output, scopeIsRetrievable: true, sizeThreshold: 10);

        // maxChars smaller than the stored content so more than one page is required.
        await sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 20);
        await sut.RetrievePageAsync(stored.ResultId, "session1", offset: 20, maxChars: 20);
        await sut.RetrievePageAsync(stored.ResultId, "session1", offset: 40, maxChars: 20);

        countingCache.CacheMisses.Should().Be(1,
            "the file must be read from disk once, on the first page, and served from cache thereafter");
    }

    private IOptionsMonitor<AppConfig> OptionsMonitor()
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        return monitor.Object;
    }

    /// <summary>
    /// A real <see cref="MemoryCache"/> wrapper that counts how many times a key was actually MISSING
    /// (i.e. how many times the underlying store had to go to disk), by intercepting
    /// <see cref="IMemoryCache.TryGetValue"/> and <see cref="IMemoryCache.CreateEntry"/> — the same two
    /// members <see cref="FileSystemToolResultStore.RetrievePageAsync"/> actually calls
    /// (<c>TryGetValue</c> then <c>Set</c>, which itself calls <c>CreateEntry</c>).
    /// </summary>
    private sealed class CountingMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());

        public int CacheMisses { get; private set; }

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public ICacheEntry CreateEntry(object key)
        {
            CacheMisses++;
            return _inner.CreateEntry(key);
        }

        public void Remove(object key) => _inner.Remove(key);

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task RetrievePageAsync_CachesAPage_RecordsTheEntrysSizeInCharacters()
    {
        // Security-review finding on PR #581: entries were cached with NO Size at all, so the
        // dedicated cache's SizeLimit (added in the same fix) could never actually be enforced — a
        // SizeLimit with no per-entry Size accounting is not a bound, it's decoration. TrackStatistics
        // surfaces CurrentEstimatedSize from the exact same internal accounting SizeLimit itself relies
        // on, so this proves the wiring directly rather than depending on MemoryCache's internal
        // eviction-timing implementation details (which proved too flaky to assert against directly).
        // SizeLimit must be configured, not just TrackStatistics — CurrentEstimatedSize otherwise
        // reports null, since the cache has no reason to accumulate a running size total at all.
        var cache = new MemoryCache(new MemoryCacheOptions { TrackStatistics = true, SizeLimit = 1_000_000 });
        var sut = new FileSystemToolResultStore(
            OptionsMonitor(),
            PermissiveAdmission.PermissiveSanitizer(),
            new DefaultContentRedactionFilter(),
            cache,
            Mock.Of<ILogger<FileSystemToolResultStore>>());

        var output = new string('x', 60);
        var stored = await sut.StoreIfLargeAsync(
            "session1", "search", null, output, scopeIsRetrievable: true, sizeThreshold: 10);

        await sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 60);

        cache.GetCurrentStatistics()!.CurrentEstimatedSize.Should().Be(60,
            "the cache entry's Size must be set to the content length, or a configured SizeLimit on " +
            "the real production cache could never actually be enforced");
    }

    [Fact]
    public async Task StoreIfLargeAsync_OutputLargerThanMaxSpillChars_TruncatesAtTheCap()
    {
        // #563: MaxSpillChars bounds disk, independent of PerResultCharLimit — an unbounded write
        // here would be the same "no silent caps" gap this cap exists to close, one layer down.
        _appConfig.AI.ContextManagement.ToolResultStorage.MaxSpillChars = 150;
        var output = new string('x', 500);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output, scopeIsRetrievable: true);

        result.SizeChars.Should().Be(150);
        var page = await _sut.RetrievePageAsync(result.ResultId, "session1", offset: 0, LargePageSize);
        page.Text.Should().Be(new string('x', 150));
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task StoreAndRetrieve_RoundTrips()
    {
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);

        page.Text.Should().Be(output);
        page.HasMore.Should().BeFalse();
        page.TotalChars.Should().Be(output.Length);
    }

    // --- Unconditional at-rest redaction (security-review finding on #563, third revision) ---
    //
    // Two earlier revisions each regressed a different guarantee. The first redacted each fetched
    // PAGE independently, gated by a flag carried alongside the content — HIGH security finding: a
    // page boundary is a character offset the caller (tool_result_fetch's own model-supplied
    // 'offset') chooses freely, so a secret split across two page boundaries matched no pattern in
    // either page and both halves came back verbatim. The second moved redaction to write time but
    // gated it on the ORIGINATING call's own classification — also HIGH, because a plain-allow call
    // (the common case) spilled raw, unscanned content, regressing the unconditional at-rest
    // redaction this store did before #563 existed at all. Redaction now happens once, always,
    // before the write, over the complete content — no gate for an adversarial classification to
    // sit outside of, and no page boundary for an adversarial offset to split across.

    private const string AwsKeyShapedSecret = "AKIAIOSFODNN7EXAMPLE";

    [Fact]
    public async Task StoreIfLargeAsync_LargeResult_StoredContentIsAlwaysRedacted()
    {
        var content = new string(' ', 90) + AwsKeyShapedSecret + new string(' ', 90);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, content, scopeIsRetrievable: true);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);

        page.Text.Should().NotContain(AwsKeyShapedSecret);
    }

    [Fact]
    public async Task RetrievePageAsync_SecretAtAPageBoundary_NeverAppearsAcrossEitherPage()
    {
        // The secret occupies content[90..110) — offset 100 (the page split below) lands squarely
        // inside it, the exact shape that defeated per-page redaction in the first revision.
        var content = new string(' ', 90) + AwsKeyShapedSecret + new string(' ', 90);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, content, scopeIsRetrievable: true);

        var first = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 100);
        var second = await _sut.RetrievePageAsync(stored.ResultId, "session1", first.NextOffset, maxChars: 100);

        (first.Text + second.Text).Should().NotContain(AwsKeyShapedSecret);
    }

    private const string InjectionMarker = "IGNORE-ALL-PREVIOUS-INSTRUCTIONS";

    private static Mock<ICompositeResponseSanitizer> SubstitutingSanitizer(string find, string replacement)
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => content.Contains(find, StringComparison.Ordinal)
                ? SanitizationResult.WithFindings(content.Replace(find, replacement), content, [])
                : SanitizationResult.Clean(content));
        return sanitizer;
    }

    [Fact]
    public async Task StoreIfLargeAsync_SanitizerReturnsEmptyContent_PersistsAPlaceholderNotEmptyContent()
    {
        // Security-review finding: ICompositeResponseSanitizer is consumer-replaceable, and
        // ToolResultText.SanitizeText already treats a non-null-in/empty-out result as a contract break
        // worth a visible placeholder — this store's own sanitize call must carry the same guarantee,
        // or a non-conforming implementation silently discards a large tool result with no trace.
        // Mutation test: removing the empty-content guard in StoreIfLargeAsync makes this assert an
        // empty string instead of the placeholder.
        var corruptedSanitizer = new Mock<ICompositeResponseSanitizer>();
        corruptedSanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.WithFindings(string.Empty, content, []));

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var store = new FileSystemToolResultStore(
            monitor.Object, corruptedSanitizer.Object, new DefaultContentRedactionFilter(),
            new MemoryCache(new MemoryCacheOptions()), Mock.Of<ILogger<FileSystemToolResultStore>>());

        var stored = await store.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);

        var page = await store.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);
        page.Text.Should().Be("[tool result withheld: the response sanitizer returned no content]");
    }

    [Fact]
    public async Task StoreIfLargeAsync_LargeResult_StoredContentIsAlwaysSanitizedForInjection()
    {
        // Security-review finding: the injection/exfiltration scan is a DIFFERENT mechanism from
        // secret redaction above, and closing the redaction boundary-split bug did nothing for it —
        // it otherwise runs once per model-facing CALL, and #563 gave a single logical result many
        // such calls once it started paginating. Sanitizing unconditionally at write time, the same
        // way redaction already is, means the stored copy itself never contains the payload — a page
        // boundary chosen anywhere (including by an adversarial caller) can only split content that is
        // already clean.
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var store = new FileSystemToolResultStore(
            monitor.Object, SubstitutingSanitizer(InjectionMarker, "[SCRUBBED]").Object,
            new DefaultContentRedactionFilter(), new MemoryCache(new MemoryCacheOptions()), Mock.Of<ILogger<FileSystemToolResultStore>>());

        var content = new string(' ', 90) + InjectionMarker + new string(' ', 90);
        var stored = await store.StoreIfLargeAsync("session1", "tool", null, content, scopeIsRetrievable: true);

        var page = await store.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);
        page.Text.Should().NotContain(InjectionMarker);
        page.Text.Should().Contain("[SCRUBBED]");
    }

    [Fact]
    public async Task RetrievePageAsync_InjectionPayloadAtAPageBoundary_NeverAppearsAcrossEitherPage()
    {
        // The payload occupies content[90..123) — offset 100 (the page split below) lands squarely
        // inside it. Mutation test: skip the sanitize call in StoreIfLargeAsync and this fails, because
        // an unscrubbed marker split across the two pages still reconstructs to the full string when
        // concatenated, exactly the shape a per-page (read-time) fix cannot close but a write-time one
        // does — no page boundary, wherever a caller chooses to put it, can split a pattern that was
        // already removed before either page existed.
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var store = new FileSystemToolResultStore(
            monitor.Object, SubstitutingSanitizer(InjectionMarker, "[SCRUBBED]").Object,
            new DefaultContentRedactionFilter(), new MemoryCache(new MemoryCacheOptions()), Mock.Of<ILogger<FileSystemToolResultStore>>());

        var content = new string(' ', 90) + InjectionMarker + new string(' ', 90);
        var stored = await store.StoreIfLargeAsync("session1", "tool", null, content, scopeIsRetrievable: true);

        var first = await store.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 100);
        var second = await store.RetrievePageAsync(stored.ResultId, "session1", first.NextOffset, maxChars: 100);

        (first.Text + second.Text).Should().NotContain(InjectionMarker);
    }

    [Fact]
    public async Task StoreIfLargeAsync_SecretStraddlingTheMaxSpillCharsCutoff_IsStillRedacted()
    {
        // /code-review finding: redaction used to run AFTER the MaxSpillChars cut, so a secret whose
        // match started before the cutoff but extended past it lost its tail before the redaction
        // filter ever saw it — the same "boundary a cut creates defeats a pattern match" shape as the
        // page-splitting bypass above, just at the write-side truncation boundary instead of a
        // read-side page boundary. MaxSpillChars=100 here puts the cutoff at char 100 -- squarely
        // inside the secret occupying content[90..110).
        _appConfig.AI.ContextManagement.ToolResultStorage.MaxSpillChars = 100;
        var content = new string(' ', 90) + AwsKeyShapedSecret;

        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, content, scopeIsRetrievable: true);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);
        page.Text.Should().NotContain(AwsKeyShapedSecret);
        page.Text.Should().NotContain("AKIAIOSFOD",
            "no raw fragment of the secret's first half may survive the cut unredacted either");
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
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

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
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var freshInstance = new FileSystemToolResultStore(
            monitor.Object, PermissiveAdmission.PermissiveSanitizer(), new DefaultContentRedactionFilter(),
            new MemoryCache(new MemoryCacheOptions()), Mock.Of<ILogger<FileSystemToolResultStore>>());

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
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

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
        var act = () => _sut.StoreIfLargeAsync("../escape", "tool", null, "data", scopeIsRetrievable: true);

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
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, "data", scopeIsRetrievable: true);

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
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, "data", scopeIsRetrievable: true);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Theory]
    // Regression: an earlier version of this allowlist excluded ':' entirely on the (incorrect) claim
    // that a bare drive reference is "drive-relative, not rooted" — Path.IsPathRooted measures "C:"
    // and "C:foo" as TRUE on Windows. But excluding ':' outright also rejected every legitimate id
    // using it as an internal separator (PlanRunKeys.StepConversationId's "{runScope}:{stepId}"),
    // failing every LLM step of every plan run. ':' is admitted by the charset; these two cases are
    // refused instead by SanitizeSessionSegment's independent Path.IsPathRooted check below.
    [InlineData("C:")]
    [InlineData("C:foo")]
    public async Task StoreIfLargeAsync_WindowsDriveRootedSessionId_ThrowsButNeverWritesOutsideStoragePath(string sessionId)
    {
        // Build-and-test finding: Path.IsPathRooted's drive-letter semantics are Windows-only —
        // Path.IsPathRooted("C:") is true on Windows (what SanitizeSessionSegment rejects here) but
        // false on Linux/macOS, where a leading '/' is the only rooted shape and the allowed charset
        // already excludes '/' entirely. This shape poses no escape risk outside Windows: it becomes an
        // ordinary, contained subdirectory name there, asserted below on both platforms alike — the
        // invariant that must hold everywhere is "never escapes the storage root", not "always throws".
        var output = new string('x', 200);
        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, output, scopeIsRetrievable: true);

        if (OperatingSystem.IsWindows())
        {
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("sessionId");
        }
        else
        {
            await act.Should().NotThrowAsync();
            // SanitizeSessionSegment replaces ':' with '~' unconditionally (Windows refuses ':' as a
            // directory-NAME character outside the drive-separator position rejected above) — the real
            // created directory is "C~"/"C~foo", not the raw sessionId.
            Directory.Exists(Path.Combine(_tempDir, sessionId.Replace(':', '~'))).Should().BeTrue();
        }

        Directory.Exists(Path.Combine(_tempDir, "..", "foo")).Should().BeFalse();
    }

    [Theory]
    // Negative controls: every character class the allowlist admits, proving the regex above isn't
    // accidentally rejecting a legitimate id shape while it's busy rejecting the bad ones. Includes
    // ':' used as an internal separator (PlanRunKeys.StepConversationId's actual production shape) and
    // a two-letter prefix before ':', both of which Path.IsPathRooted measures as NOT rooted — only a
    // single-letter prefix (a real drive letter) is.
    [InlineData("conversation-id-123")]
    [InlineData("plan_run.42")]
    [InlineData("ABCDEFabcdef0123456789")]
    [InlineData("scope:with:colons")]
    [InlineData("conv-1:step-5")]
    [InlineData("AB:foo")]
    public async Task StoreIfLargeAsync_SessionIdWithinTheAllowedCharset_DoesNotThrow(string sessionId)
    {
        // Correctness-review finding: content must exceed this fixture's 100-char PerResultCharLimit
        // so StoreIfLargeAsync actually reaches CreateDirectoryOwnerOnly/Path.Combine. A short "data"
        // payload stays inline and never touches the filesystem, so a colon-in-the-charset id would
        // pass this test even on a build where Directory.CreateDirectory("conv-1:step-5") throws
        // IOException on Windows — which the pre-fix code did.
        var output = new string('x', 200);

        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, output, scopeIsRetrievable: true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StoreIfLargeAsync_SessionIdAtTheWorstCasePlanStepLength_DoesNotThrow()
    {
        // Regression: an earlier version of this allowlist bounded length at 128 to match
        // IPlanRunExecutor.MaxAgentIdLength (the cap on a bare run scope), but
        // PlanRunKeys.StepConversationId derives "{runScope}:{stepId}" from that value, up to
        // 128 + 1 (':') + 36 (a Guid's default ToString length) = 165 characters — which the
        // 128-char bound rejected outright for any run scope over 91 characters.
        var derivedId = $"{new string('a', 128)}:{Guid.NewGuid()}";
        derivedId.Length.Should().Be(165, "the test must exercise the actual worst case, not an approximation");

        // Must exceed PerResultCharLimit — see the allowed-charset theory above for why a short
        // payload would silently skip the disk write this test exists to exercise.
        var output = new string('x', 200);

        var result = await _sut.StoreIfLargeAsync(derivedId, "tool", null, output, scopeIsRetrievable: true);

        result.IsPersistedToDisk.Should().BeTrue();
    }

    [Fact]
    public async Task StoreIfLargeAsync_SessionIdEndingInNewline_ThrowsArgumentException()
    {
        // Security-review finding: "$" in .NET regex matches immediately before a trailing '\n', not
        // only at the true end of the string, so "^[...]+$" admitted a value ending in a newline — a
        // caller-supplied id that becomes a directory name and a log line. The rest of the id ("conv-1")
        // is otherwise entirely within the charset, so this isolates the anchor fix specifically rather
        // than incidentally failing on the space character a less careful test string would introduce.
        // Fixed by anchoring with \A/\z instead.
        var act = () => _sut.StoreIfLargeAsync("conv-1\n", "tool", null, "data", scopeIsRetrievable: true);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
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
        var act = () => _sut.StoreIfLargeAsync("session1", "tool", null, null!, scopeIsRetrievable: true);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StoreIfLargeAsync_EmptySessionId_ThrowsArgumentException()
    {
        var act = () => _sut.StoreIfLargeAsync("", "tool", null, "data", scopeIsRetrievable: true);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- Pagination (#563) -----------------------------------------------------------------

    [Fact]
    public async Task RetrievePageAsync_OffsetBeyondTheEnd_ReturnsEmptyWithHasMoreFalse()
    {
        // Must exceed this fixture's 100-char PerResultCharLimit or StoreIfLargeAsync keeps it inline
        // and there is no file for RetrievePageAsync to read.
        var output = new string('a', 150);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

        var page = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 1_000, maxChars: 10);

        page.Text.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.TotalChars.Should().Be(150);
    }

    [Fact]
    public async Task RetrievePageAsync_WalkingEveryPage_ReassemblesTextIdenticalToWhatWasStored()
    {
        var output = string.Concat(Enumerable.Range(0, 500).Select(i => (char)('a' + i % 26)));
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

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
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output, scopeIsRetrievable: true);

        var first = await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, maxChars: 91);
        var second = await _sut.RetrievePageAsync(stored.ResultId, "session1", first.NextOffset, maxChars: 100);

        (first.Text + second.Text).Should().Be(output);
        first.Text.Should().NotEndWith("\uD83D"); // lone high surrogate would prove the pair was split
    }

    // --- Retention sweep (#559) -------------------------------------------------------------

    [Fact]
    public async Task PruneExpiredAsync_FileOlderThanGracePeriod_IsDeleted()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);
        File.SetLastWriteTimeUtc(stored.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(1);
        File.Exists(stored.FullContentPath!).Should().BeFalse();
    }

    [Fact]
    public async Task PruneExpiredAsync_DeletesTheBackingFile_AlsoEvictsAnyCachedPage()
    {
        // #574 code-review finding: the page-fetch cache (5-minute sliding expiration) is populated by
        // ANY prior RetrievePageAsync call and was never invalidated when the backing file was later
        // reclaimed — a fetch within that window could keep serving "reclaimed" content past the point
        // retention was supposed to make it unrecoverable. Warm the cache first, then prune, then prove
        // the very next fetch sees the deletion instead of a stale cache hit.
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);
        await _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize); // warms the cache
        File.SetLastWriteTimeUtc(stored.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));

        await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        var act = () => _sut.RetrievePageAsync(stored.ResultId, "session1", offset: 0, LargePageSize);
        await act.Should().ThrowAsync<KeyNotFoundException>(
            "the cache must not keep serving a result whose backing file retention already reclaimed");
    }

    [Fact]
    public async Task PruneExpiredAsync_FileWithinGracePeriod_IsKept()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);

        var removed = await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        removed.Should().Be(0, "the file was just written — well within a one-day grace period");
        File.Exists(stored.FullContentPath!).Should().BeTrue();
    }

    [Fact]
    public async Task PruneExpiredAsync_RemovesTheNowEmptyScopeAndToolResultsDirectories()
    {
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);
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
        var stale = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);
        File.SetLastWriteTimeUtc(stale.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));
        var fresh = await _sut.StoreIfLargeAsync("session2", "tool", null, new string('b', 200), scopeIsRetrievable: true);

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

    [Fact]
    public async Task PruneExpiredAsync_StoragePathHasTrailingSeparator_DoesNotClimbPastTheConfiguredRoot()
    {
        // Security-review finding: Path.GetFullPath preserves a trailing separator on its input but
        // Path.GetDirectoryName (what RemoveIfEmptyUpTo climbs with) always strips one — measured
        // directly against this SDK (GetFullPath("C:/a/") keeps the trailing '\', GetFullPath("C:/a")
        // does not) — so a StoragePath configured WITH a trailing separator made the "never climbs to
        // or above root" equality check never fire, and a fully-swept scope kept deleting empty
        // ancestors past the root the caller configured, including the root itself.
        var storageRoot = Path.Combine(_tempDir, "with-trailing-sep");
        Directory.CreateDirectory(storageRoot);
        _appConfig.AI.ContextManagement.ToolResultStorage.StoragePath = storageRoot + Path.DirectorySeparatorChar;

        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, new string('a', 200), scopeIsRetrievable: true);
        File.SetLastWriteTimeUtc(stored.FullContentPath!, DateTime.UtcNow - TimeSpan.FromDays(2));

        await _sut.PruneExpiredAsync(TimeSpan.FromDays(1));

        Directory.Exists(storageRoot).Should().BeTrue(
            "the sweep must stop at the configured storage root, not climb past it just because the " +
            "config value happened to carry a trailing separator");
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
