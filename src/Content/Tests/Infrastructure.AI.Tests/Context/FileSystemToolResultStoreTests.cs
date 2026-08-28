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
    public async Task StoreAndRetrieve_RoundTrips()
    {
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var retrieved = await _sut.RetrieveFullContentAsync(stored.ResultId, "session1");

        retrieved.Should().Be(output);
    }

    [Fact]
    public async Task RetrieveFullContentAsync_MissingId_ThrowsKeyNotFoundException()
    {
        var act = () => _sut.RetrieveFullContentAsync("nonexistent-id", "session1");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RetrieveFullContentAsync_CorrectIdWrongScope_ThrowsKeyNotFoundException()
    {
        // #521: the retrieval scope must match the write scope. A different (but otherwise valid)
        // scopeId must be refused identically to a resultId that was never stored at all — see
        // IToolResultStore.RetrieveFullContentAsync's own remarks for why the two must be
        // indistinguishable from outside the store.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var act = () => _sut.RetrieveFullContentAsync(stored.ResultId, "session2");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RetrieveFullContentAsync_AfterANewStoreInstance_StillFindsTheResult()
    {
        // #521: the path is reconstructed deterministically from (scopeId, resultId), not trusted from
        // an in-memory index — proving this needs a genuinely SEPARATE store instance (a fresh process
        // would build its own empty index), not just a second call on the same _sut.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);
        var freshInstance = new FileSystemToolResultStore(monitor.Object, Mock.Of<ILogger<FileSystemToolResultStore>>());

        var retrieved = await freshInstance.RetrieveFullContentAsync(stored.ResultId, "session1");

        retrieved.Should().Be(output);
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
    public async Task RetrieveFullContentAsync_ResultIdThatIsNotAMintedId_ThrowsKeyNotFoundException(string resultId)
    {
        // resultId is model-supplied (ToolResultFetchTool hands the LLM's own argument straight to this
        // method), so an unsanitized one is a path-traversal sink: sanitizing scopeId alone leaves the
        // isolation boundary reachable by writing the traversal into the OTHER path segment instead.
        // Mutation test: delete the Guid.TryParseExact guard in RetrieveFullContentAsync and the first
        // two cases retrieve session1's data from session2's scope.
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var act = () => _sut.RetrieveFullContentAsync(
            resultId.Replace("PLACEHOLDER", stored.ResultId, StringComparison.Ordinal), "session2");

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

    [Fact]
    public async Task RetrieveFullContentAsync_TrailingSpaceInScopeId_ThrowsArgumentExceptionNamingScopeId()
    {
        // Same collision guard, exercised through the retrieval side — and confirms the exception names
        // the CALLING method's own parameter (scopeId), not a copy-pasted "sessionId" from the sibling
        // method that shares this validation helper (a /code-review finding).
        var act = () => _sut.RetrieveFullContentAsync(Guid.NewGuid().ToString("N"), "session1 ");

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
