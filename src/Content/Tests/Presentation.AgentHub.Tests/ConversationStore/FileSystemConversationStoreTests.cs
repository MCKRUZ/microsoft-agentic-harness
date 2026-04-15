using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Models;
using Presentation.AgentHub.Services;

namespace Presentation.AgentHub.Tests.ConversationStore;

/// <summary>Unit tests for <see cref="FileSystemConversationStore"/> using a temp directory fixture.</summary>
public sealed class FileSystemConversationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemConversationStore _store;

    public FileSystemConversationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        var config = Options.Create(new AgentHubConfig
        {
            ConversationsPath = _tempDir,
            DefaultAgentName = "test-agent",
            MaxHistoryMessages = 20,
        });

        _store = new FileSystemConversationStore(config, NullLogger<FileSystemConversationStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_WritesJsonFileAtExpectedPath()
    {
        var record = await _store.CreateAsync("agent", "user1");

        var expectedPath = Path.Combine(_tempDir, $"{record.Id}.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownConversationId()
    {
        var result = await _store.GetAsync(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_DeserializesConversationRecordCorrectly()
    {
        var created = await _store.CreateAsync("my-agent", "user-abc");

        var retrieved = await _store.GetAsync(created.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal("my-agent", retrieved.AgentName);
        Assert.Equal("user-abc", retrieved.UserId);
        Assert.Empty(retrieved.Messages);
    }

    [Fact]
    public async Task AppendMessageAsync_UpdatesFileAtomically()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var message = new ConversationMessage(MessageRole.User, "hello", DateTimeOffset.UtcNow);

        await _store.AppendMessageAsync(record.Id, message);

        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");

        Assert.True(File.Exists(filePath));
        Assert.Empty(tmpFiles);

        var updated = await _store.GetAsync(record.Id);
        Assert.NotNull(updated);
        Assert.Single(updated.Messages);
        Assert.Equal("hello", updated.Messages[0].Content);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");
        Assert.True(File.Exists(filePath));

        await _store.DeleteAsync(record.Id);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyConversationsBelongingToGivenUserId()
    {
        await _store.CreateAsync("agent", "user-a");
        await _store.CreateAsync("agent", "user-a");
        await _store.CreateAsync("agent", "user-b");

        var userAConversations = await _store.ListAsync("user-a");
        var userBConversations = await _store.ListAsync("user-b");

        Assert.Equal(2, userAConversations.Count);
        Assert.Single(userBConversations);
        Assert.All(userAConversations, c => Assert.Equal("user-a", c.UserId));
        Assert.All(userBConversations, c => Assert.Equal("user-b", c.UserId));
    }

    [Fact]
    public async Task ConcurrentAppendMessageAsync_OnSameConversationId_DoesNotCorruptFile()
    {
        const int messageCount = 20;
        var record = await _store.CreateAsync("agent", "user1");

        var tasks = Enumerable.Range(0, messageCount)
            .Select(i => _store.AppendMessageAsync(
                record.Id,
                new ConversationMessage(MessageRole.User, $"message-{i}", DateTimeOffset.UtcNow)));

        await Task.WhenAll(tasks);

        var updated = await _store.GetAsync(record.Id);
        Assert.NotNull(updated);
        Assert.Equal(messageCount, updated.Messages.Count);
    }

    [Fact]
    public async Task PathWithTraversalCharacters_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.GetAsync("../evil"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.DeleteAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task GetHistoryForDispatch_ReturnsAtMostMaxMessages()
    {
        var record = await _store.CreateAsync("agent", "user1");

        for (var i = 0; i < 15; i++)
            await _store.AppendMessageAsync(record.Id,
                new ConversationMessage(MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));

        var history = await _store.GetHistoryForDispatch(record.Id, maxMessages: 5);

        Assert.NotNull(history);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public async Task GetHistoryForDispatch_ReturnsLastNMessages_NotFirstN()
    {
        var record = await _store.CreateAsync("agent", "user1");

        for (var i = 0; i < 30; i++)
            await _store.AppendMessageAsync(record.Id,
                new ConversationMessage(MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));

        var history = await _store.GetHistoryForDispatch(record.Id, maxMessages: 10);

        Assert.NotNull(history);
        Assert.Equal(10, history.Count);
        // The last 10 messages should be msg-20 through msg-29
        Assert.Equal("msg-20", history[0].Content);
        Assert.Equal("msg-29", history[9].Content);
    }
}
