using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The behaviour every <see cref="IConversationStore"/> implementation owes its callers, asserted
/// against each one.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations are registered behind this interface — the file-backed store and the SQLite
/// one — and which is live is a config switch. A suite written against only one of them would let
/// the other diverge silently, and the divergence would surface as a transcript that reads back
/// wrong on a consumer's machine rather than as a red test here. Everything in this class is
/// expressed purely through the interface for that reason; anything that needs to reach behind it
/// (a file on disk, a row in a table) belongs in the implementation's own suite.
/// </para>
/// <para>
/// Derive, hand back a store from <see cref="Store"/>, and every test below runs against it.
/// </para>
/// </remarks>
public abstract class ConversationStoreContractTests
{
    /// <summary>The implementation under test. Derived fixtures build and own it.</summary>
    protected abstract IConversationStore Store { get; }

    /// <summary>The instant <see cref="Clock"/> is pinned to before a test advances it.</summary>
    protected static readonly DateTimeOffset FixedNow = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The clock every store under test must be built with, so timestamp behaviour can be asserted
    /// rather than raced against the wall clock.
    /// </summary>
    /// <remarks>
    /// Owned here rather than left abstract for the fixtures to supply. An abstract member lets a
    /// fixture hand back a clock that is not the one it passed to its store — which compiles, reads
    /// correctly, and makes the timestamp tests assert nothing. Base initializers run before the
    /// derived constructor body, so a fixture can pass this straight into the store it builds.
    /// </remarks>
    protected FakeTimeProvider Clock { get; } = new(FixedNow);

    // -- Timestamps --

    [Fact]
    public async Task CreateAsync_StampsTimesFromTheInjectedClock()
    {
        // Both implementations must answer to the host's clock, not to DateTimeOffset.UtcNow. A
        // store that reads the wall clock directly still passes every other test in this suite,
        // which is exactly why this one exists.
        var record = await Store.CreateAsync("agent", "user1");

        record.CreatedAt.Should().Be(FixedNow);
        record.UpdatedAt.Should().Be(FixedNow);
        (await Store.GetAsync(record.Id))!.CreatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task AppendMessage_AdvancesUpdatedAtButNotCreatedAt()
    {
        var record = await Store.CreateAsync("agent", "user1");
        Clock.Advance(TimeSpan.FromMinutes(5));

        await Store.AppendMessageAsync(record.Id, UserMessage("hello"));

        var updated = (await Store.GetAsync(record.Id))!;
        updated.CreatedAt.Should().Be(FixedNow, "creation time is history, not state");
        updated.UpdatedAt.Should().Be(FixedNow.AddMinutes(5));
    }

    // -- CreateAsync / GetAsync --

    [Fact]
    public async Task GetAsync_UnknownConversationId_ReturnsNull()
    {
        var result = await Store.GetAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_AfterCreate_ReturnsTheRecordAsCreated()
    {
        var created = await Store.CreateAsync("my-agent", "user-abc");

        var retrieved = await Store.GetAsync(created.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(created.Id);
        retrieved.AgentName.Should().Be("my-agent");
        retrieved.UserId.Should().Be("user-abc");
        retrieved.Messages.Should().BeEmpty();
        retrieved.Title.Should().BeNull();
        retrieved.Settings.Should().BeNull();
        retrieved.Telemetry.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithExplicitId_UsesProvidedId()
    {
        var explicitId = Guid.NewGuid().ToString();

        var record = await Store.CreateAsync("agent", "user1", conversationId: explicitId);

        record.Id.Should().Be(explicitId);
        (await Store.GetAsync(explicitId)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithNullId_GeneratesNewId()
    {
        var record = await Store.CreateAsync("agent", "user1", conversationId: null);

        record.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(record.Id, out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WithAnIdThatAlreadyExists_ReplacesTheRecord()
    {
        // Defined rather than left to diverge: writing a record's file overwrites whatever was
        // there, so the SQLite store has to replace too or the same call would throw on one
        // provider and succeed on the other. Both callers reach CreateAsync only after a Get
        // returned nothing, so nothing in the harness takes this path today.
        var id = Guid.NewGuid().ToString();
        await Store.CreateAsync("agent", "user1", conversationId: id);
        await Store.AppendMessageAsync(id, UserMessage("original"));

        var replaced = await Store.CreateAsync("other-agent", "user2", conversationId: id);

        replaced.AgentName.Should().Be("other-agent");
        var retrieved = await Store.GetAsync(id);
        retrieved!.UserId.Should().Be("user2");
        retrieved.Messages.Should().BeEmpty("a replaced conversation does not inherit the old transcript");
    }

    // -- ListAsync --

    [Fact]
    public async Task ListAsync_ReturnsOnlyConversationsOwnedByTheGivenUser()
    {
        var userA = $"user-a-{Guid.NewGuid():N}";
        var userB = $"user-b-{Guid.NewGuid():N}";
        await Store.CreateAsync("agent", userA);
        await Store.CreateAsync("agent", userA);
        await Store.CreateAsync("agent", userB);

        var forA = await Store.ListAsync(userA);
        var forB = await Store.ListAsync(userB);

        forA.Should().HaveCount(2).And.OnlyContain(c => c.UserId == userA);
        forB.Should().ContainSingle().Which.UserId.Should().Be(userB);
    }

    [Fact]
    public async Task ListAsync_IncludesEachConversationsMessages()
    {
        var userId = $"user-{Guid.NewGuid():N}";
        var record = await Store.CreateAsync("agent", userId);
        await Store.AppendMessageAsync(record.Id, UserMessage("first"));
        await Store.AppendMessageAsync(record.Id, AssistantMessage("second"));

        var listed = await Store.ListAsync(userId);

        listed.Should().ContainSingle();
        listed[0].Messages.Select(m => m.Content).Should().Equal("first", "second");
    }

    [Fact]
    public async Task ListAsync_UnknownUser_ReturnsEmpty()
    {
        var result = await Store.ListAsync($"nobody-{Guid.NewGuid():N}");

        result.Should().BeEmpty();
    }

    // -- AppendMessageAsync --

    [Fact]
    public async Task AppendMessage_MakesTheMessageReadable()
    {
        var record = await Store.CreateAsync("agent", "user1");

        await Store.AppendMessageAsync(record.Id, UserMessage("hello"));

        var updated = await Store.GetAsync(record.Id);
        updated!.Messages.Should().ContainSingle();
        updated.Messages[0].Content.Should().Be("hello");
        updated.Messages[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public async Task AppendMessage_PreservesAppendOrder()
    {
        var record = await Store.CreateAsync("agent", "user1");

        for (var i = 0; i < 12; i++)
            await Store.AppendMessageAsync(record.Id, UserMessage($"msg-{i}"));

        var updated = await Store.GetAsync(record.Id);
        updated!.Messages.Select(m => m.Content)
            .Should().Equal(Enumerable.Range(0, 12).Select(i => $"msg-{i}"));
    }

    [Fact]
    public async Task AppendMessage_NonexistentConversation_ThrowsInvalidOperationException()
    {
        var act = () => Store.AppendMessageAsync("nonexistent", UserMessage("msg"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendMessage_WithEmptyMessageId_ReadsBackAStableNonEmptyId()
    {
        // Clients may append without supplying an id. Whichever store is live, the message has to
        // come back with a real one — a retry or edit references a message by id, and Guid.Empty
        // would either collide with every other id-less message or match nothing at all.
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(
            record.Id,
            new ConversationMessage(Guid.Empty, MessageRole.User, "no id", DateTimeOffset.UtcNow));

        var first = await Store.GetAsync(record.Id);
        var second = await Store.GetAsync(record.Id);

        first!.Messages[0].Id.Should().NotBe(Guid.Empty);
        second!.Messages[0].Id.Should().Be(first.Messages[0].Id, "the assigned id must survive a reload");
    }

    [Fact]
    public async Task AppendMessage_RoundTripsToolCallsAndWidget()
    {
        var record = await Store.CreateAsync("agent", "user1");
        var toolCall = new ToolCallRecord(
            "search",
            JsonDocument.Parse("""{"query":"weather"}""").RootElement,
            JsonDocument.Parse("""{"result":["sunny"]}""").RootElement,
            DurationMs: 42);
        var widget = new WidgetSpec("render_table", JsonDocument.Parse("""{"rows":[1,2]}""").RootElement);

        await Store.AppendMessageAsync(
            record.Id,
            new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow,
                ToolCalls: [toolCall], Widget: widget));

        var message = (await Store.GetAsync(record.Id))!.Messages.Single();
        message.ToolCalls.Should().ContainSingle();
        message.ToolCalls![0].ToolName.Should().Be("search");
        message.ToolCalls[0].DurationMs.Should().Be(42);
        message.ToolCalls[0].Input.GetRawText().Should().Contain("weather");
        message.Widget.Should().NotBeNull();
        message.Widget!.Type.Should().Be("render_table");
        message.Widget.Args.GetRawText().Should().Contain("rows");
    }

    [Fact]
    public async Task ConcurrentAppends_ToOneConversation_AllSurvive()
    {
        const int messageCount = 20;
        var record = await Store.CreateAsync("agent", "user1");

        await Task.WhenAll(Enumerable.Range(0, messageCount)
            .Select(i => Store.AppendMessageAsync(record.Id, UserMessage($"message-{i}"))));

        var updated = await Store.GetAsync(record.Id);
        updated!.Messages.Should().HaveCount(messageCount);
        updated.Messages.Select(m => m.Content).Should().OnlyHaveUniqueItems(
            "a lost update shows up as one message overwritten by another, not as a missing row");
    }

    // -- Title derivation --

    [Fact]
    public async Task AppendMessage_FirstUserMessage_DerivesTitleFromContent()
    {
        var record = await Store.CreateAsync("agent", "user1");

        await Store.AppendMessageAsync(record.Id, UserMessage("What is the meaning of life?"));

        (await Store.GetAsync(record.Id))!.Title.Should().Be("What is the meaning of life?");
    }

    [Fact]
    public async Task AppendMessage_AssistantMessageFirst_DoesNotDeriveTitle()
    {
        var record = await Store.CreateAsync("agent", "user1");

        await Store.AppendMessageAsync(record.Id, AssistantMessage("I am an assistant"));

        (await Store.GetAsync(record.Id))!.Title.Should().BeNull();
    }

    [Fact]
    public async Task AppendMessage_SubsequentUserMessage_DoesNotOverrideExistingTitle()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("First question"));
        await Store.AppendMessageAsync(record.Id, UserMessage("Second question"));

        (await Store.GetAsync(record.Id))!.Title.Should().Be("First question");
    }

    // -- TruncateFromMessageAsync --

    [Fact]
    public async Task TruncateFromMessage_RemovesTargetAndEverythingAfterIt()
    {
        var record = await Store.CreateAsync("agent", "user1");
        var msg1 = UserMessage("Hello");
        var msg2 = AssistantMessage("Hi!");
        var msg3 = UserMessage("More");
        await Store.AppendMessageAsync(record.Id, msg1);
        await Store.AppendMessageAsync(record.Id, msg2);
        await Store.AppendMessageAsync(record.Id, msg3);

        var truncated = await Store.TruncateFromMessageAsync(record.Id, msg2.Id);

        truncated.Should().NotBeNull();
        truncated!.Messages.Should().ContainSingle().Which.Id.Should().Be(msg1.Id);
        (await Store.GetAsync(record.Id))!.Messages.Should().ContainSingle(
            "the truncation has to be persisted, not just reflected in the returned record");
    }

    [Fact]
    public async Task TruncateFromMessage_FirstMessage_RemovesAll()
    {
        var record = await Store.CreateAsync("agent", "user1");
        var msg1 = UserMessage("First");
        await Store.AppendMessageAsync(record.Id, msg1);
        await Store.AppendMessageAsync(record.Id, AssistantMessage("Second"));

        var truncated = await Store.TruncateFromMessageAsync(record.Id, msg1.Id);

        truncated!.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateFromMessage_UnknownMessage_ReturnsRecordUnchanged()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("Hello"));

        var result = await Store.TruncateFromMessageAsync(record.Id, Guid.NewGuid());

        result.Should().NotBeNull();
        result!.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task TruncateFromMessage_UnknownConversation_ReturnsNull()
    {
        var result = await Store.TruncateFromMessageAsync("does-not-exist", Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task TruncateFromMessage_ThenAppend_ContinuesAfterTheSurvivingMessages()
    {
        // The retry flow's actual shape: drop the superseded tail, then re-dispatch. If the new
        // message sorted before the survivors the model would be handed a scrambled transcript.
        var record = await Store.CreateAsync("agent", "user1");
        var keep = UserMessage("keep me");
        var drop = AssistantMessage("drop me");
        await Store.AppendMessageAsync(record.Id, keep);
        await Store.AppendMessageAsync(record.Id, drop);
        await Store.TruncateFromMessageAsync(record.Id, drop.Id);

        await Store.AppendMessageAsync(record.Id, AssistantMessage("replacement"));

        (await Store.GetAsync(record.Id))!.Messages.Select(m => m.Content)
            .Should().Equal("keep me", "replacement");
    }

    // -- UpdateSettingsAsync --

    [Fact]
    public async Task UpdateSettings_PersistsSettings()
    {
        var record = await Store.CreateAsync("agent", "user1");

        var updated = await Store.UpdateSettingsAsync(record.Id, new ConversationSettings("gpt-4", 0.7f, "Be helpful."));

        updated!.Settings!.DeploymentName.Should().Be("gpt-4");
        updated.Settings.Temperature.Should().Be(0.7f);
        updated.Settings.SystemPromptOverride.Should().Be("Be helpful.");
    }

    [Fact]
    public async Task UpdateSettings_OverwritesPreviousSettings()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.UpdateSettingsAsync(record.Id, new ConversationSettings("gpt-4", 0.5f, null));
        await Store.UpdateSettingsAsync(record.Id, new ConversationSettings("claude", 0.9f, "New prompt"));

        var retrieved = await Store.GetAsync(record.Id);

        retrieved!.Settings!.DeploymentName.Should().Be("claude");
        retrieved.Settings.Temperature.Should().Be(0.9f);
        retrieved.Settings.SystemPromptOverride.Should().Be("New prompt");
    }

    [Fact]
    public async Task UpdateSettings_AllFieldsNull_StillReadsBackAsSettingsPresent()
    {
        // "No settings, use the provider defaults" and "settings that override nothing" are
        // different states, and the store must not collapse the second into the first.
        var record = await Store.CreateAsync("agent", "user1");

        await Store.UpdateSettingsAsync(record.Id, new ConversationSettings(null, null, null));

        (await Store.GetAsync(record.Id))!.Settings.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateSettings_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.UpdateSettingsAsync("missing", new ConversationSettings(null, null, null));

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSettings_DoesNotDisturbTheTranscript()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("hello"));

        var updated = await Store.UpdateSettingsAsync(record.Id, new ConversationSettings("gpt-4", null, null));

        updated!.Messages.Should().ContainSingle().Which.Content.Should().Be("hello");
    }

    // -- UpdateTelemetryAsync --

    [Fact]
    public async Task UpdateTelemetry_PersistsSessionIdAndAccumulator()
    {
        var record = await Store.CreateAsync("agent", "user1");
        var sessionId = Guid.NewGuid();
        var telemetry = TelemetryAccumulator.Zero.Add(
            inputTokens: 100, outputTokens: 50, cacheRead: 10, cacheWrite: 5, costUsd: 0.25m, toolCalls: 2);

        await Store.UpdateTelemetryAsync(record.Id, sessionId, telemetry);

        var retrieved = await Store.GetAsync(record.Id);
        retrieved!.ObservabilitySessionId.Should().Be(sessionId);
        retrieved.Telemetry.Should().NotBeNull();
        retrieved.Telemetry!.TurnCount.Should().Be(1);
        retrieved.Telemetry.ToolCallCount.Should().Be(2);
        retrieved.Telemetry.InputTokens.Should().Be(100);
        retrieved.Telemetry.OutputTokens.Should().Be(50);
        retrieved.Telemetry.CacheRead.Should().Be(10);
        retrieved.Telemetry.CacheWrite.Should().Be(5);
        retrieved.Telemetry.CostUsd.Should().Be(0.25m);
    }

    [Fact]
    public async Task UpdateTelemetry_AccumulatesAcrossTurns()
    {
        // The point of persisting telemetry is that the stateless handler can carry a running total
        // across requests. A store that dropped the previous value would leave every turn reporting
        // as if it were the first.
        var record = await Store.CreateAsync("agent", "user1");
        var sessionId = Guid.NewGuid();
        var afterTurn1 = TelemetryAccumulator.Zero.Add(100, 50, 0, 0, 0.10m, 1);
        await Store.UpdateTelemetryAsync(record.Id, sessionId, afterTurn1);

        var reloaded = (await Store.GetAsync(record.Id))!.Telemetry!;
        await Store.UpdateTelemetryAsync(record.Id, sessionId, reloaded.Add(20, 10, 0, 0, 0.05m, 0));

        var final = (await Store.GetAsync(record.Id))!.Telemetry!;
        final.TurnCount.Should().Be(2);
        final.InputTokens.Should().Be(120);
        final.CostUsd.Should().Be(0.15m);
    }

    [Fact]
    public async Task UpdateTelemetry_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.UpdateTelemetryAsync("missing", Guid.NewGuid(), TelemetryAccumulator.Zero);

        result.Should().BeNull();
    }

    // -- DeleteAsync --

    [Fact]
    public async Task Delete_RemovesTheConversation()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("hello"));

        await Store.DeleteAsync(record.Id);

        (await Store.GetAsync(record.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonexistentConversation_DoesNotThrow()
    {
        var act = () => Store.DeleteAsync("nonexistent");

        await act.Should().NotThrowAsync();
    }

    // -- GetHistoryForDispatch --

    [Fact]
    public async Task GetHistoryForDispatch_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.GetHistoryForDispatch("nonexistent", 10);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryForDispatch_FewerMessagesThanMax_ReturnsAll()
    {
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("msg-1"));
        await Store.AppendMessageAsync(record.Id, UserMessage("msg-2"));

        var result = await Store.GetHistoryForDispatch(record.Id, 10);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHistoryForDispatch_ReturnsTheLastNMessagesInOrder()
    {
        var record = await Store.CreateAsync("agent", "user1");
        for (var i = 0; i < 30; i++)
            await Store.AppendMessageAsync(record.Id, UserMessage($"msg-{i}"));

        var history = await Store.GetHistoryForDispatch(record.Id, maxMessages: 10);

        history.Should().HaveCount(10);
        history!.Select(m => m.Content).Should().Equal(
            Enumerable.Range(20, 10).Select(i => $"msg-{i}"),
            "dispatch needs the most recent turns, oldest first — the window is a tail, not a head");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetHistoryForDispatch_NonPositiveMax_ReturnsNoMessages(int maxMessages)
    {
        // A window of "no messages" must not mean "every message". SQLite reads a negative LIMIT as
        // no limit at all, so a store that passes the value straight through would hand the model
        // the entire transcript — the opposite of what the caller asked for, and unbounded.
        var record = await Store.CreateAsync("agent", "user1");
        for (var i = 0; i < 5; i++)
            await Store.AppendMessageAsync(record.Id, UserMessage($"msg-{i}"));

        var history = await Store.GetHistoryForDispatch(record.Id, maxMessages);

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryForDispatch_ExcludesEmptyContentMessages()
    {
        // A widget-only message carries its payload in the spec, not in text, so it is not
        // model-relevant. Counting it against the window would let widgets crowd real turns out.
        var record = await Store.CreateAsync("agent", "user1");
        await Store.AppendMessageAsync(record.Id, UserMessage("real question"));
        await Store.AppendMessageAsync(
            record.Id,
            new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow,
                Widget: new WidgetSpec("render_table", JsonDocument.Parse("{}").RootElement)));
        await Store.AppendMessageAsync(record.Id, AssistantMessage("real answer"));

        var history = await Store.GetHistoryForDispatch(record.Id, maxMessages: 10);

        history!.Select(m => m.Content).Should().Equal("real question", "real answer");
    }

    // -- Helpers --

    /// <summary>Builds a user message with a fresh id and the current timestamp.</summary>
    protected static ConversationMessage UserMessage(string content) =>
        new(Guid.NewGuid(), MessageRole.User, content, DateTimeOffset.UtcNow);

    /// <summary>Builds an assistant message with a fresh id and the current timestamp.</summary>
    protected static ConversationMessage AssistantMessage(string content) =>
        new(Guid.NewGuid(), MessageRole.Assistant, content, DateTimeOffset.UtcNow);
}
