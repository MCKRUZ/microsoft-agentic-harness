using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.ConversationStore;

public sealed class FileSystemConversationStoreAdditionalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemConversationStore _store;

    public FileSystemConversationStoreAdditionalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"convstore-{Guid.NewGuid():N}");
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

    // -- TruncateFromMessageAsync --

    [Fact]
    public async Task TruncateFromMessage_RemovesTargetAndSubsequentMessages()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var msg1 = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Hello", DateTimeOffset.UtcNow);
        var msg2 = new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "Hi!", DateTimeOffset.UtcNow);
        var msg3 = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "More", DateTimeOffset.UtcNow);

        await _store.AppendMessageAsync(record.Id, msg1);
        await _store.AppendMessageAsync(record.Id, msg2);
        await _store.AppendMessageAsync(record.Id, msg3);

        var truncated = await _store.TruncateFromMessageAsync(record.Id, msg2.Id);

        truncated.Should().NotBeNull();
        truncated!.Messages.Should().HaveCount(1);
        truncated.Messages[0].Id.Should().Be(msg1.Id);
    }

    [Fact]
    public async Task TruncateFromMessage_NonexistentMessage_ReturnsUnchangedRecord()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var msg1 = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Hello", DateTimeOffset.UtcNow);
        await _store.AppendMessageAsync(record.Id, msg1);

        var result = await _store.TruncateFromMessageAsync(record.Id, Guid.NewGuid());

        result.Should().NotBeNull();
        result!.Messages.Should().HaveCount(1);
    }

    [Fact]
    public async Task TruncateFromMessage_NonexistentConversation_ReturnsNull()
    {
        var result = await _store.TruncateFromMessageAsync("does-not-exist", Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task TruncateFromMessage_FirstMessage_RemovesAll()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var msg1 = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "First", DateTimeOffset.UtcNow);
        var msg2 = new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant, "Second", DateTimeOffset.UtcNow);

        await _store.AppendMessageAsync(record.Id, msg1);
        await _store.AppendMessageAsync(record.Id, msg2);

        var truncated = await _store.TruncateFromMessageAsync(record.Id, msg1.Id);

        truncated.Should().NotBeNull();
        truncated!.Messages.Should().BeEmpty();
    }

    // -- UpdateSettingsAsync --

    [Fact]
    public async Task UpdateSettings_PersistsSettingsToConversation()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var settings = new ConversationSettings("gpt-4", 0.7f, "Be helpful.");

        var updated = await _store.UpdateSettingsAsync(record.Id, settings);

        updated.Should().NotBeNull();
        updated!.Settings.Should().NotBeNull();
        updated.Settings!.DeploymentName.Should().Be("gpt-4");
        updated.Settings.Temperature.Should().Be(0.7f);
        updated.Settings.SystemPromptOverride.Should().Be("Be helpful.");
    }

    [Fact]
    public async Task UpdateSettings_NonexistentConversation_ReturnsNull()
    {
        var result = await _store.UpdateSettingsAsync("missing", new ConversationSettings(null, null, null));

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSettings_OverwritesPreviousSettings()
    {
        var record = await _store.CreateAsync("agent", "user1");
        await _store.UpdateSettingsAsync(record.Id, new ConversationSettings("gpt-4", 0.5f, null));
        await _store.UpdateSettingsAsync(record.Id, new ConversationSettings("claude", 0.9f, "New prompt"));

        var retrieved = await _store.GetAsync(record.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Settings!.DeploymentName.Should().Be("claude");
        retrieved.Settings.Temperature.Should().Be(0.9f);
        retrieved.Settings.SystemPromptOverride.Should().Be("New prompt");
    }

    // -- CreateAsync with explicit ID --

    [Fact]
    public async Task CreateAsync_WithExplicitId_UsesProvidedId()
    {
        var explicitId = Guid.NewGuid().ToString();

        var record = await _store.CreateAsync("agent", "user1", conversationId: explicitId);

        record.Id.Should().Be(explicitId);

        var retrieved = await _store.GetAsync(explicitId);
        retrieved.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithNullId_GeneratesNewId()
    {
        var record = await _store.CreateAsync("agent", "user1", conversationId: null);

        record.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(record.Id, out _).Should().BeTrue();
    }

    // -- AppendMessageAsync title derivation --

    [Fact]
    public async Task AppendMessage_FirstUserMessage_DerivesTitleFromContent()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var msg = new ConversationMessage(Guid.NewGuid(), MessageRole.User,
            "What is the meaning of life?", DateTimeOffset.UtcNow);

        await _store.AppendMessageAsync(record.Id, msg);

        var updated = await _store.GetAsync(record.Id);
        updated.Should().NotBeNull();
        updated!.Title.Should().Be("What is the meaning of life?");
    }

    [Fact]
    public async Task AppendMessage_AssistantMessageOnEmptyTitle_DoesNotDeriveTitle()
    {
        var record = await _store.CreateAsync("agent", "user1");
        var msg = new ConversationMessage(Guid.NewGuid(), MessageRole.Assistant,
            "I am an assistant", DateTimeOffset.UtcNow);

        await _store.AppendMessageAsync(record.Id, msg);

        var updated = await _store.GetAsync(record.Id);
        updated.Should().NotBeNull();
        updated!.Title.Should().BeNull();
    }

    [Fact]
    public async Task AppendMessage_SubsequentUserMessage_DoesNotOverrideExistingTitle()
    {
        var record = await _store.CreateAsync("agent", "user1");
        await _store.AppendMessageAsync(record.Id,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "First question", DateTimeOffset.UtcNow));
        await _store.AppendMessageAsync(record.Id,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "Second question", DateTimeOffset.UtcNow));

        var updated = await _store.GetAsync(record.Id);
        updated!.Title.Should().Be("First question");
    }

    // -- GetHistoryForDispatch --

    [Fact]
    public async Task GetHistoryForDispatch_NonexistentConversation_ReturnsNull()
    {
        var result = await _store.GetHistoryForDispatch("nonexistent", 10);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryForDispatch_FewerMessagesThanMax_ReturnsAll()
    {
        var record = await _store.CreateAsync("agent", "user1");
        await _store.AppendMessageAsync(record.Id,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "msg-1", DateTimeOffset.UtcNow));
        await _store.AppendMessageAsync(record.Id,
            new ConversationMessage(Guid.NewGuid(), MessageRole.User, "msg-2", DateTimeOffset.UtcNow));

        var result = await _store.GetHistoryForDispatch(record.Id, 10);

        result.Should().HaveCount(2);
    }

    // -- AppendMessageAsync on non-existent conversation --

    [Fact]
    public async Task AppendMessage_NonexistentConversation_ThrowsInvalidOperationException()
    {
        var msg = new ConversationMessage(Guid.NewGuid(), MessageRole.User, "msg", DateTimeOffset.UtcNow);

        var act = () => _store.AppendMessageAsync("nonexistent", msg);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // -- Delete non-existent --

    [Fact]
    public async Task Delete_NonexistentConversation_DoesNotThrow()
    {
        var act = () => _store.DeleteAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }
}
