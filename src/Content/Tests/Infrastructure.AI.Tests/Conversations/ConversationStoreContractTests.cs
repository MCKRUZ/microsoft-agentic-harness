using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.Common.Exceptions.ExceptionTypes;
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
/// <para>
/// Every call passes a caller id explicitly rather than through a helper that fills one in. The
/// identity is the thing under test in the ownership section, and a suite that supplied it out of
/// sight would still be green if the parameter were ignored entirely.
/// </para>
/// </remarks>
public abstract class ConversationStoreContractTests
{
    /// <summary>The implementation under test. Derived fixtures build and own it.</summary>
    protected abstract IConversationStore Store { get; }

    /// <summary>The instant <see cref="Clock"/> is pinned to before a test advances it.</summary>
    protected static readonly DateTimeOffset FixedNow = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The owner used by tests that are not about ownership.</summary>
    protected const string Owner = "user1";

    /// <summary>A second, unrelated caller — the one every refusal below is asserted against.</summary>
    protected const string Stranger = "user2";

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

    // -- Ownership --
    //
    // The store enforces ownership itself. It did not always: the comparison lived in six places
    // across four files, and a seventh entry point was about to be added without one. These tests
    // are the reason a caller can no longer forget it, so each must fail if the check is removed —
    // not merely pass while the check happens to be there.

    [Fact]
    public async Task GetAsync_ConversationOwnedByAnotherUser_IsRefused()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var act = () => Store.GetAsync(record.Id, Stranger);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
    }

    [Fact]
    public async Task GetAsync_UnknownConversation_ReadsAsAbsentRatherThanRefused()
    {
        // Absent and forbidden stay distinguishable because the HTTP surface distinguishes them.
        // Collapsing both into a refusal would also make every unknown id look like someone else's.
        var result = await Store.GetAsync($"missing-{Guid.NewGuid():N}", Stranger);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AppendMessage_ToAnotherUsersConversation_IsRefusedAndWritesNothing()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var act = () => Store.AppendMessageAsync(record.Id, Stranger, UserMessage("not mine"));

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().BeEmpty(
            "a refused append must not half-apply — the header must not move either");
    }

    [Fact]
    public async Task Delete_OfAnotherUsersConversation_IsRefusedAndLeavesItIntact()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("still here"));

        var act = () => Store.DeleteAsync(record.Id, Stranger);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task TruncateFromMessage_OnAnotherUsersConversation_IsRefused()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var message = UserMessage("mine");
        await Store.AppendMessageAsync(record.Id, Owner, message);

        var act = () => Store.TruncateFromMessageAsync(record.Id, Stranger, message.Id);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateSettings_OnAnotherUsersConversation_IsRefused()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var act = () => Store.UpdateSettingsAsync(
            record.Id, Stranger, new ConversationSettings("gpt-4", null, null));

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(record.Id, Owner))!.Settings.Should().BeNull();
    }

    [Fact]
    public async Task UpdateTelemetry_OnAnotherUsersConversation_IsRefused()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var act = () => Store.UpdateTelemetryAsync(
            record.Id, Stranger, Guid.NewGuid(), TelemetryAccumulator.Zero);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
    }

    [Fact]
    public async Task GetHistoryForDispatch_OnAnotherUsersConversation_IsRefused()
    {
        // The path that actually feeds a model. A miss here would not merely leak a read — it would
        // put someone else's transcript into a prompt.
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("private"));

        var act = () => Store.GetHistoryForDispatch(record.Id, Stranger, 10);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
    }

    [Fact]
    public async Task CreateAsync_ReusingAnotherUsersConversationId_IsRefusedAndLeavesItIntact()
    {
        // Create replaces, so without this a caller could name any id, destroy the transcript behind
        // it, and take the id over. It was unreachable only because every caller happened to check
        // ownership first — the arrangement this interface stopped relying on.
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("mine"));

        var act = () => Store.CreateAsync("agent", Stranger, conversationId: record.Id);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        var survivor = (await Store.GetAsync(record.Id, Owner))!;
        survivor.UserId.Should().Be(Owner);
        survivor.Messages.Should().ContainSingle().Which.Content.Should().Be("mine");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EveryOperation_WithABlankCallerId_IsRejectedRatherThanTreatedAsUnscoped(string blank)
    {
        // Fail closed. A blank identity is what arrives when a claim did not resolve, and this
        // codebase has three recorded incidents of that absence being read as "everyone". It has to
        // be an error at the boundary, never a value that flows on and widens access.
        var record = await Store.CreateAsync("agent", Owner);

        // Named rather than a bare run of assertions: every operation has to hold the line, and when
        // one does not the failure should say which. "Some call in here threw the wrong thing" is a
        // poor message for the test guarding the codebase's most repeated security defect.
        (string Name, Func<Task> Invoke)[] operations =
        [
            ("GetAsync", () => Store.GetAsync(record.Id, blank)),
            ("ListAsync", () => Store.ListAsync(blank)),
            ("CreateAsync", () => Store.CreateAsync("agent", blank)),
            ("AppendMessageAsync", () => Store.AppendMessageAsync(record.Id, blank, UserMessage("x"))),
            ("DeleteAsync", () => Store.DeleteAsync(record.Id, blank)),
            ("TruncateFromMessageAsync", () => Store.TruncateFromMessageAsync(record.Id, blank, Guid.NewGuid())),
            ("UpdateSettingsAsync",
                () => Store.UpdateSettingsAsync(record.Id, blank, new ConversationSettings(null, null, null))),
            ("UpdateTelemetryAsync",
                () => Store.UpdateTelemetryAsync(record.Id, blank, Guid.NewGuid(), TelemetryAccumulator.Zero)),
            ("GetHistoryForDispatch", () => Store.GetHistoryForDispatch(record.Id, blank, 10)),
        ];

        foreach (var (name, invoke) in operations)
        {
            var act = () => invoke();
            await act.Should().ThrowAsync<ArgumentException>(
                "{0} must reject a blank caller id rather than treat it as unscoped", name);
        }
    }

    // -- Timestamps --

    [Fact]
    public async Task CreateAsync_StampsTimesFromTheInjectedClock()
    {
        // Both implementations must answer to the host's clock, not to DateTimeOffset.UtcNow. A
        // store that reads the wall clock directly still passes every other test in this suite,
        // which is exactly why this one exists.
        var record = await Store.CreateAsync("agent", Owner);

        record.CreatedAt.Should().Be(FixedNow);
        record.UpdatedAt.Should().Be(FixedNow);
        (await Store.GetAsync(record.Id, Owner))!.CreatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task AppendMessage_AdvancesUpdatedAtButNotCreatedAt()
    {
        var record = await Store.CreateAsync("agent", Owner);
        Clock.Advance(TimeSpan.FromMinutes(5));

        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("hello"));

        var updated = (await Store.GetAsync(record.Id, Owner))!;
        updated.CreatedAt.Should().Be(FixedNow, "creation time is history, not state");
        updated.UpdatedAt.Should().Be(FixedNow.AddMinutes(5));
    }

    // -- CreateAsync / GetAsync --

    [Fact]
    public async Task GetAsync_UnknownConversationId_ReturnsNull()
    {
        var result = await Store.GetAsync(Guid.NewGuid().ToString(), Owner);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_AfterCreate_ReturnsTheRecordAsCreated()
    {
        var created = await Store.CreateAsync("my-agent", "user-abc");

        var retrieved = await Store.GetAsync(created.Id, "user-abc");

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

        var record = await Store.CreateAsync("agent", Owner, conversationId: explicitId);

        record.Id.Should().Be(explicitId);
        (await Store.GetAsync(explicitId, Owner)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithNullId_GeneratesNewId()
    {
        var record = await Store.CreateAsync("agent", Owner, conversationId: null);

        record.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(record.Id, out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WithAnIdThatAlreadyExists_ReplacesTheRecordForItsOwner()
    {
        // Defined rather than left to diverge: writing a record's file overwrites whatever was
        // there, so the SQLite store has to replace too or the same call would throw on one
        // provider and succeed on the other. Replacement is confined to the owner's own
        // conversations — the cross-owner case is refused, and is asserted above.
        var id = Guid.NewGuid().ToString();
        await Store.CreateAsync("agent", Owner, conversationId: id);
        await Store.AppendMessageAsync(id, Owner, UserMessage("original"));

        var replaced = await Store.CreateAsync("other-agent", Owner, conversationId: id);

        replaced.AgentName.Should().Be("other-agent");
        var retrieved = await Store.GetAsync(id, Owner);
        retrieved!.AgentName.Should().Be("other-agent");
        retrieved.Messages.Should().BeEmpty("a replaced conversation does not inherit the old transcript");
    }

    // -- GetOrCreateAsync --
    //
    // The operation exists because the obvious composition — read, then create when the read came back
    // empty — is a transcript-destroying race, since CreateAsync REPLACES. The tests that matter here
    // are therefore the ones asserting what it must never do.

    [Fact]
    public async Task GetOrCreate_UnknownConversation_CreatesItUnderTheGivenIdAndOwner()
    {
        var id = $"conv-{Guid.NewGuid():N}";

        var record = await Store.GetOrCreateAsync("agent", Owner, id);

        record.Id.Should().Be(id);
        record.UserId.Should().Be(Owner);
        record.AgentName.Should().Be("agent");
        record.Messages.Should().BeEmpty();
        (await Store.GetAsync(id, Owner)).Should().NotBeNull("the conversation must actually be stored");
    }

    [Fact]
    public async Task GetOrCreate_ExistingConversation_ReturnsItWithItsTranscriptIntact()
    {
        // The whole point of the operation. If this ever returns an empty record, a second run
        // continuing a conversation silently starts it over — and CreateAsync would have done exactly
        // that, which is why this is not simply a call to CreateAsync.
        var created = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(created.Id, Owner, UserMessage("first turn"));

        var reopened = await Store.GetOrCreateAsync("agent", Owner, created.Id);

        reopened.Messages.Should().ContainSingle().Which.Content.Should().Be("first turn");
    }

    [Fact]
    public async Task GetOrCreate_ExistingConversation_DoesNotDestroyItEvenWhenTheAgentDiffers()
    {
        // A caller naming a different agent must not be read as "make me a new one". CreateAsync's
        // replace semantics make that the natural mistake for an implementation to inherit.
        var created = await Store.CreateAsync("original-agent", Owner);
        await Store.AppendMessageAsync(created.Id, Owner, UserMessage("keep me"));

        var reopened = await Store.GetOrCreateAsync("a-different-agent", Owner, created.Id);

        reopened.AgentName.Should().Be("original-agent", "an existing conversation keeps its own agent");
        reopened.Messages.Should().ContainSingle().Which.Content.Should().Be("keep me");
    }

    [Fact]
    public async Task GetOrCreate_ConversationOwnedByAnotherUser_IsRefusedAndLeavesItIntact()
    {
        // Without this, the operation is a way to take over any id you can guess: the refusal is what
        // stops a stranger's conversation being replaced by an empty one they then own.
        var created = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(created.Id, Owner, UserMessage("mine"));

        var act = () => Store.GetOrCreateAsync("agent", Stranger, created.Id);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(created.Id, Owner))!.Messages.Should().ContainSingle(
            "a refused open must not have replaced the conversation");
    }

    [Fact]
    public async Task GetOrCreate_BlankCallerId_IsRejectedRatherThanTreatedAsGlobal()
    {
        var act = () => Store.GetOrCreateAsync("agent", "  ", $"conv-{Guid.NewGuid():N}");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetOrCreate_BlankConversationId_IsRejected()
    {
        // Distinct from CreateAsync, where an absent id means "mint me one". Here the id is the thing
        // being opened, so a blank one has no reading that is not a caller bug.
        var act = () => Store.GetOrCreateAsync("agent", Owner, "  ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentOpensOfOneNewConversation_AllAgreeAndNoneDestroysTheTranscript()
    {
        // Twenty callers open the same brand-new id at once. Exactly one create can win, and every
        // loser must be handed the winner's record instead of failing or replacing it — an
        // implementation that inserts without handling the collision leaves nineteen of these throwing.
        //
        // What this does NOT prove, stated so nobody reads more into a green run than is there: it
        // cannot force a loser's write to land AFTER a message has been appended, which is the exact
        // interleaving that makes a read-then-create implementation destroy a transcript. All twenty
        // opens have completed before anything is appended here. The deterministic half of that
        // guarantee is covered by the two "existing conversation" tests above; this one covers the
        // concurrent half only as far as a test without a seam into the store can reach.
        var id = $"conv-{Guid.NewGuid():N}";

        var opens = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => Store.GetOrCreateAsync("agent", Owner, id)))
            .ToArray();

        var records = await Task.WhenAll(opens);

        records.Should().OnlyContain(r => r.Id == id && r.UserId == Owner);

        await Store.AppendMessageAsync(id, Owner, UserMessage("survived"));
        (await Store.GetAsync(id, Owner))!.Messages.Should().ContainSingle();
    }

    // -- AppendMessagesAsync --
    //
    // The batch exists so a complete exchange is stored as one unit. All-or-nothing is the property
    // that matters: this transcript is replayed to a model, so a half-written turn is not an incomplete
    // record but a misleading one.

    [Fact]
    public async Task AppendMessages_StoresThemAllInOrder()
    {
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessagesAsync(record.Id, Owner, [
            UserMessage("what is my name?"),
            AssistantMessage("Sam"),
        ]);

        (await Store.GetAsync(record.Id, Owner))!.Messages.Select(m => m.Content).Should().Equal(
            "what is my name?", "Sam");
    }

    [Fact]
    public async Task AppendMessages_EmptyBatch_IsANoOp()
    {
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessagesAsync(record.Id, Owner, []);

        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendMessages_ToAnotherUsersConversation_IsRefusedAndWritesNothing()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var act = () => Store.AppendMessagesAsync(record.Id, Stranger, [
            UserMessage("not mine"),
            AssistantMessage("nor this"),
        ]);

        await act.Should().ThrowAsync<ConversationAccessDeniedException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendMessages_ToAnUnknownConversation_FailsAndWritesNothing()
    {
        var act = () => Store.AppendMessagesAsync($"missing-{Guid.NewGuid():N}", Owner, [
            UserMessage("nowhere to go"),
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendMessages_DuplicateIdInsideTheBatch_IsRejectedAndNothingIsWritten()
    {
        // The whole batch is refused, not merely the offending message. A partially applied batch is
        // the failure this operation exists to prevent, so failing halfway would be worse than the two
        // separate appends it replaces.
        var record = await Store.CreateAsync("agent", Owner);
        var shared = Guid.NewGuid();

        var act = () => Store.AppendMessagesAsync(record.Id, Owner, [
            new ConversationMessage(shared, MessageRole.User, "first", Clock.GetUtcNow()),
            new ConversationMessage(shared, MessageRole.Assistant, "second", Clock.GetUtcNow()),
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().BeEmpty(
            "a batch that cannot be stored whole must not be stored in part");
    }

    [Fact]
    public async Task AppendMessages_IdAlreadyInTheConversation_IsRejectedAndTheEarlierBatchSurvives()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var first = UserMessage("already here");
        await Store.AppendMessagesAsync(record.Id, Owner, [first]);

        var act = () => Store.AppendMessagesAsync(record.Id, Owner, [
            AssistantMessage("fine on its own"),
            new ConversationMessage(first.Id, MessageRole.User, "a replay", Clock.GetUtcNow()),
        ]);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Select(m => m.Content).Should().Equal(
            "already here");
    }

    [Fact]
    public async Task AppendMessages_DerivesTheTitleFromTheFirstUserMessageInTheBatch()
    {
        // Same rule the single append follows, so a turn stored as one batch and the same turn stored
        // as two appends cannot end up with different titles.
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessagesAsync(record.Id, Owner, [
            UserMessage("the opening question"),
            AssistantMessage("the reply"),
        ]);

        (await Store.GetAsync(record.Id, Owner))!.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AppendMessages_BlankCallerId_IsRejectedRatherThanTreatedAsGlobal()
    {
        var act = () => Store.AppendMessagesAsync("any", "  ", [UserMessage("nope")]);

        await act.Should().ThrowAsync<ArgumentException>();
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
        await Store.AppendMessageAsync(record.Id, userId, UserMessage("first"));
        await Store.AppendMessageAsync(record.Id, userId, AssistantMessage("second"));

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
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("hello"));

        var updated = await Store.GetAsync(record.Id, Owner);
        updated!.Messages.Should().ContainSingle();
        updated.Messages[0].Content.Should().Be("hello");
        updated.Messages[0].Role.Should().Be(MessageRole.User);
    }

    [Fact]
    public async Task AppendMessage_PreservesAppendOrder()
    {
        var record = await Store.CreateAsync("agent", Owner);

        for (var i = 0; i < 12; i++)
            await Store.AppendMessageAsync(record.Id, Owner, UserMessage($"msg-{i}"));

        var updated = await Store.GetAsync(record.Id, Owner);
        updated!.Messages.Select(m => m.Content)
            .Should().Equal(Enumerable.Range(0, 12).Select(i => $"msg-{i}"));
    }

    [Fact]
    public async Task AppendMessage_NonexistentConversation_ThrowsInvalidOperationException()
    {
        var act = () => Store.AppendMessageAsync("nonexistent", Owner, UserMessage("msg"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendMessage_DuplicateMessageId_ThrowsAndLeavesTheTranscriptIntact()
    {
        // Message ids come from the client, so a double-submitted or replayed turn arrives carrying
        // an id already in the transcript. Both stores must refuse it the same way: two rows sharing
        // one id make a retry's cut point arbitrary, because truncation resolves an id to one match.
        // Refusing must also be a defined failure, not whichever exception the storage engine threw.
        var record = await Store.CreateAsync("agent", Owner);
        var message = UserMessage("first submit");
        await Store.AppendMessageAsync(record.Id, Owner, message);

        var act = () => Store.AppendMessageAsync(record.Id, Owner, message);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().ContainSingle(
            "a rejected append must not half-apply");
    }

    [Fact]
    public async Task AppendMessage_SameIdInADifferentConversation_IsAllowed()
    {
        // The id only has to be unique within one conversation. Rejecting it globally would make two
        // unrelated transcripts able to collide.
        var first = await Store.CreateAsync("agent", Owner);
        var second = await Store.CreateAsync("agent", Owner);
        var message = UserMessage("same id, different conversation");

        await Store.AppendMessageAsync(first.Id, Owner, message);
        var act = () => Store.AppendMessageAsync(second.Id, Owner, message);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AppendMessage_WithEmptyMessageId_ReadsBackAStableNonEmptyId()
    {
        // Clients may append without supplying an id. Whichever store is live, the message has to
        // come back with a real one — a retry or edit references a message by id, and Guid.Empty
        // would either collide with every other id-less message or match nothing at all.
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(
            record.Id,
            Owner,
            new ConversationMessage(Guid.Empty, MessageRole.User, "no id", DateTimeOffset.UtcNow));

        var first = await Store.GetAsync(record.Id, Owner);
        var second = await Store.GetAsync(record.Id, Owner);

        first!.Messages[0].Id.Should().NotBe(Guid.Empty);
        second!.Messages[0].Id.Should().Be(first.Messages[0].Id, "the assigned id must survive a reload");
    }

    [Fact]
    public async Task AppendMessage_RoundTripsToolCallsAndWidget()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var toolCall = new ToolCallRecord(
            "search",
            """{"query":"weather"}""",
            """{"result":["sunny"]}""",
            DurationMs: 42,
            CallId: "call-1",
            RoundOrdinal: 0);
        var widget = new WidgetSpec("render_table", JsonDocument.Parse("""{"rows":[1,2]}""").RootElement);

        await Store.AppendMessageAsync(
            record.Id,
            Owner,
            new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow,
                ToolCalls: [toolCall], Widget: widget));

        var message = (await Store.GetAsync(record.Id, Owner))!.Messages.Single();
        message.ToolCalls.Should().ContainSingle();
        message.ToolCalls![0].ToolName.Should().Be("search");
        message.ToolCalls[0].DurationMs.Should().Be(42);
        message.ToolCalls[0].Input.Should().Contain("weather");
        message.ToolCalls[0].CallId.Should().Be("call-1");
        message.ToolCalls[0].RoundOrdinal.Should().Be(0);
        message.Widget.Should().NotBeNull();
        message.Widget!.Type.Should().Be("render_table");
        message.Widget.Args.GetRawText().Should().Contain("rows");
    }

    [Fact]
    public async Task ConcurrentAppends_ToOneConversation_AllSurvive()
    {
        const int messageCount = 20;
        var record = await Store.CreateAsync("agent", Owner);

        await Task.WhenAll(Enumerable.Range(0, messageCount)
            .Select(i => Store.AppendMessageAsync(record.Id, Owner, UserMessage($"message-{i}"))));

        var updated = await Store.GetAsync(record.Id, Owner);
        updated!.Messages.Should().HaveCount(messageCount);
        updated.Messages.Select(m => m.Content).Should().OnlyHaveUniqueItems(
            "a lost update shows up as one message overwritten by another, not as a missing row");
    }

    // -- Title derivation --

    [Fact]
    public async Task AppendMessage_FirstUserMessage_DerivesTitleFromContent()
    {
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("What is the meaning of life?"));

        (await Store.GetAsync(record.Id, Owner))!.Title.Should().Be("What is the meaning of life?");
    }

    [Fact]
    public async Task AppendMessage_AssistantMessageFirst_DoesNotDeriveTitle()
    {
        var record = await Store.CreateAsync("agent", Owner);

        await Store.AppendMessageAsync(record.Id, Owner, AssistantMessage("I am an assistant"));

        (await Store.GetAsync(record.Id, Owner))!.Title.Should().BeNull();
    }

    [Fact]
    public async Task AppendMessage_SubsequentUserMessage_DoesNotOverrideExistingTitle()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("First question"));
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("Second question"));

        (await Store.GetAsync(record.Id, Owner))!.Title.Should().Be("First question");
    }

    // -- TruncateFromMessageAsync --

    [Fact]
    public async Task TruncateFromMessage_RemovesTargetAndEverythingAfterIt()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var msg1 = UserMessage("Hello");
        var msg2 = AssistantMessage("Hi!");
        var msg3 = UserMessage("More");
        await Store.AppendMessageAsync(record.Id, Owner, msg1);
        await Store.AppendMessageAsync(record.Id, Owner, msg2);
        await Store.AppendMessageAsync(record.Id, Owner, msg3);

        var truncated = await Store.TruncateFromMessageAsync(record.Id, Owner, msg2.Id);

        truncated.Should().NotBeNull();
        truncated!.Messages.Should().ContainSingle().Which.Id.Should().Be(msg1.Id);
        (await Store.GetAsync(record.Id, Owner))!.Messages.Should().ContainSingle(
            "the truncation has to be persisted, not just reflected in the returned record");
    }

    [Fact]
    public async Task TruncateFromMessage_FirstMessage_RemovesAll()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var msg1 = UserMessage("First");
        await Store.AppendMessageAsync(record.Id, Owner, msg1);
        await Store.AppendMessageAsync(record.Id, Owner, AssistantMessage("Second"));

        var truncated = await Store.TruncateFromMessageAsync(record.Id, Owner, msg1.Id);

        truncated!.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateFromMessage_UnknownMessage_ReturnsRecordUnchanged()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("Hello"));

        var result = await Store.TruncateFromMessageAsync(record.Id, Owner, Guid.NewGuid());

        result.Should().NotBeNull();
        result!.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task TruncateFromMessage_UnknownConversation_ReturnsNull()
    {
        var result = await Store.TruncateFromMessageAsync("does-not-exist", Owner, Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task TruncateFromMessage_ThenAppend_ContinuesAfterTheSurvivingMessages()
    {
        // The retry flow's actual shape: drop the superseded tail, then re-dispatch. If the new
        // message sorted before the survivors the model would be handed a scrambled transcript.
        var record = await Store.CreateAsync("agent", Owner);
        var keep = UserMessage("keep me");
        var drop = AssistantMessage("drop me");
        await Store.AppendMessageAsync(record.Id, Owner, keep);
        await Store.AppendMessageAsync(record.Id, Owner, drop);
        await Store.TruncateFromMessageAsync(record.Id, Owner, drop.Id);

        await Store.AppendMessageAsync(record.Id, Owner, AssistantMessage("replacement"));

        (await Store.GetAsync(record.Id, Owner))!.Messages.Select(m => m.Content)
            .Should().Equal("keep me", "replacement");
    }

    // -- UpdateSettingsAsync --

    [Fact]
    public async Task UpdateSettings_PersistsSettings()
    {
        var record = await Store.CreateAsync("agent", Owner);

        var updated = await Store.UpdateSettingsAsync(
            record.Id, Owner, new ConversationSettings("gpt-4", 0.7f, "Be helpful."));

        updated!.Settings!.DeploymentName.Should().Be("gpt-4");
        updated.Settings.Temperature.Should().Be(0.7f);
        updated.Settings.SystemPromptOverride.Should().Be("Be helpful.");
    }

    [Fact]
    public async Task UpdateSettings_OverwritesPreviousSettings()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.UpdateSettingsAsync(record.Id, Owner, new ConversationSettings("gpt-4", 0.5f, null));
        await Store.UpdateSettingsAsync(record.Id, Owner, new ConversationSettings("claude", 0.9f, "New prompt"));

        var retrieved = await Store.GetAsync(record.Id, Owner);

        retrieved!.Settings!.DeploymentName.Should().Be("claude");
        retrieved.Settings.Temperature.Should().Be(0.9f);
        retrieved.Settings.SystemPromptOverride.Should().Be("New prompt");
    }

    [Fact]
    public async Task UpdateSettings_AllFieldsNull_StillReadsBackAsSettingsPresent()
    {
        // "No settings, use the provider defaults" and "settings that override nothing" are
        // different states, and the store must not collapse the second into the first.
        var record = await Store.CreateAsync("agent", Owner);

        await Store.UpdateSettingsAsync(record.Id, Owner, new ConversationSettings(null, null, null));

        (await Store.GetAsync(record.Id, Owner))!.Settings.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateSettings_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.UpdateSettingsAsync(
            "missing", Owner, new ConversationSettings(null, null, null));

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSettings_DoesNotDisturbTheTranscript()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("hello"));

        var updated = await Store.UpdateSettingsAsync(
            record.Id, Owner, new ConversationSettings("gpt-4", null, null));

        updated!.Messages.Should().ContainSingle().Which.Content.Should().Be("hello");
    }

    // -- UpdateTelemetryAsync --

    [Fact]
    public async Task UpdateTelemetry_PersistsSessionIdAndAccumulator()
    {
        var record = await Store.CreateAsync("agent", Owner);
        var sessionId = Guid.NewGuid();
        var telemetry = TelemetryAccumulator.Zero.Add(
            inputTokens: 100, outputTokens: 50, cacheRead: 10, cacheWrite: 5, costUsd: 0.25m, toolCalls: 2);

        await Store.UpdateTelemetryAsync(record.Id, Owner, sessionId, telemetry);

        var retrieved = await Store.GetAsync(record.Id, Owner);
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
        var record = await Store.CreateAsync("agent", Owner);
        var sessionId = Guid.NewGuid();
        var afterTurn1 = TelemetryAccumulator.Zero.Add(100, 50, 0, 0, 0.10m, 1);
        await Store.UpdateTelemetryAsync(record.Id, Owner, sessionId, afterTurn1);

        var reloaded = (await Store.GetAsync(record.Id, Owner))!.Telemetry!;
        await Store.UpdateTelemetryAsync(record.Id, Owner, sessionId, reloaded.Add(20, 10, 0, 0, 0.05m, 0));

        var final = (await Store.GetAsync(record.Id, Owner))!.Telemetry!;
        final.TurnCount.Should().Be(2);
        final.InputTokens.Should().Be(120);
        final.CostUsd.Should().Be(0.15m);
    }

    [Fact]
    public async Task UpdateTelemetry_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.UpdateTelemetryAsync(
            "missing", Owner, Guid.NewGuid(), TelemetryAccumulator.Zero);

        result.Should().BeNull();
    }

    // -- DeleteAsync --

    [Fact]
    public async Task Delete_RemovesTheConversation()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("hello"));

        var deleted = await Store.DeleteAsync(record.Id, Owner);

        deleted.Should().BeTrue();
        (await Store.GetAsync(record.Id, Owner)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonexistentConversation_ReportsNothingDeleted()
    {
        // Reported rather than thrown, because the caller has a legitimate use for the difference:
        // the REST surface answers 404 from it without a second read to establish existence.
        var deleted = await Store.DeleteAsync($"nonexistent-{Guid.NewGuid():N}", Owner);

        deleted.Should().BeFalse();
    }

    // -- GetHistoryForDispatch --

    [Fact]
    public async Task GetHistoryForDispatch_NonexistentConversation_ReturnsNull()
    {
        var result = await Store.GetHistoryForDispatch("nonexistent", Owner, 10);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryForDispatch_FewerMessagesThanMax_ReturnsAll()
    {
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("msg-1"));
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("msg-2"));

        var result = await Store.GetHistoryForDispatch(record.Id, Owner, 10);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHistoryForDispatch_ReturnsTheLastNMessagesInOrder()
    {
        var record = await Store.CreateAsync("agent", Owner);
        for (var i = 0; i < 30; i++)
            await Store.AppendMessageAsync(record.Id, Owner, UserMessage($"msg-{i}"));

        var history = await Store.GetHistoryForDispatch(record.Id, Owner, maxMessages: 10);

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
        var record = await Store.CreateAsync("agent", Owner);
        for (var i = 0; i < 5; i++)
            await Store.AppendMessageAsync(record.Id, Owner, UserMessage($"msg-{i}"));

        var history = await Store.GetHistoryForDispatch(record.Id, Owner, maxMessages);

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryForDispatch_ExcludesEmptyContentMessages()
    {
        // A widget-only message carries its payload in the spec, not in text, so it is not
        // model-relevant. Counting it against the window would let widgets crowd real turns out.
        var record = await Store.CreateAsync("agent", Owner);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("real question"));
        await Store.AppendMessageAsync(
            record.Id,
            Owner,
            new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow,
                Widget: new WidgetSpec("render_table", JsonDocument.Parse("{}").RootElement)));
        await Store.AppendMessageAsync(record.Id, Owner, AssistantMessage("real answer"));

        var history = await Store.GetHistoryForDispatch(record.Id, Owner, maxMessages: 10);

        history!.Select(m => m.Content).Should().Equal("real question", "real answer");
    }

    [Fact]
    public async Task GetHistoryForDispatch_KeepsToolOnlyRowWithEmptyContent()
    {
        // #249 item 6: a turn that ends in tool activity with no final prose (empty Content, non-null
        // ToolCalls) is model-relevant — unlike a widget-only row — so it must survive the same filter
        // that drops widget rows, or the tool call/result pair this feature exists to replay silently
        // disappears from every replayed window.
        var record = await Store.CreateAsync("agent", Owner);
        var toolCall = new ToolCallRecord("search", null, "sunny", DurationMs: 1, CallId: "call-1", RoundOrdinal: 0);
        await Store.AppendMessageAsync(record.Id, Owner, UserMessage("what's the weather?"));
        await Store.AppendMessageAsync(
            record.Id,
            Owner,
            new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow,
                ToolCalls: [toolCall]));

        var history = await Store.GetHistoryForDispatch(record.Id, Owner, maxMessages: 10);

        history.Should().HaveCount(2);
        history![1].Content.Should().BeEmpty();
        history[1].ToolCalls.Should().ContainSingle().Which.CallId.Should().Be("call-1");
    }

    // -- Helpers --

    /// <summary>Builds a user message with a fresh id and the current timestamp.</summary>
    protected static ConversationMessage UserMessage(string content) =>
        new(Guid.NewGuid(), MessageRole.User, content, DateTimeOffset.UtcNow);

    /// <summary>Builds an assistant message with a fresh id and the current timestamp.</summary>
    protected static ConversationMessage AssistantMessage(string content) =>
        new(Guid.NewGuid(), MessageRole.Assistant, content, DateTimeOffset.UtcNow);
}
