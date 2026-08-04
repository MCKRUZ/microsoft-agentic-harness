using System.Text.Json;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services;
using Application.AI.Common.Models.Conversations;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// SQLite-backed conversation store. Each message is its own row, so appending a turn is an
/// <c>INSERT</c> rather than a rewrite of the whole transcript.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this replaces the file-backed store as the default.</strong>
/// <see cref="FileSystemConversationStore"/> appends by reading a whole JSON record, adding one
/// message, and rewriting the file through a shared temporary path. That is safe only while a
/// single process does it: two processes can interleave bytes into the same staging file and move a
/// torn record into place, and two concurrent appends can lose one of the messages outright. Here
/// the corruption and lost-update paths do not exist to be mitigated — a message row is inserted and
/// nothing rereads-and-rewrites the transcript. SQLite serialises writers across processes on one
/// machine through its own file locking, which is the same guarantee the planner's durable state
/// already relies on. It does not extend across machines; a horizontally scaled deployment needs a
/// server-backed implementation behind <see cref="IConversationStore"/>.
/// </para>
/// <para>
/// <strong>Header mutations are direct <c>UPDATE</c> statements.</strong> Every method that changes
/// the conversation row uses <c>ExecuteUpdateAsync</c> rather than loading, mutating, and saving an
/// entity. That keeps each mutation a single statement touching only the columns it owns, so two
/// callers writing different columns cannot overwrite each other with stale values — the failure a
/// read-modify-write invites. The row count the statement returns doubles as the existence check.
/// </para>
/// <para>
/// <strong>Ownership is part of the statement, not a step before it.</strong> Every mutation carries
/// <c>UserId == callerId</c> in its <c>WHERE</c> clause, so the check and the write are one atomic
/// operation against the database. Checking first and writing second — what each caller used to do —
/// leaves a window in which the row can change between the two, and it costs an extra round trip on
/// the success path. Here the cost is inverted: nothing extra is read unless the statement matched
/// no rows, and only then is a probe issued to tell "no such conversation" apart from "not yours".
/// </para>
/// </remarks>
public sealed class EfCoreConversationStore : IConversationStore
{
    private readonly IDbContextFactory<ConversationDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfCoreConversationStore> _logger;

    /// <summary>
    /// Initializes the store.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived contexts, one per operation.</param>
    /// <param name="timeProvider">Clock used for <c>CreatedAt</c>/<c>UpdatedAt</c> stamps.</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="schemaInitializer">
    /// Demanded as a plain constructor dependency so that resolving this store forces the schema to
    /// be created exactly once, before the first query can hit a missing table. The instance itself
    /// is not used — construction is the whole effect, the same wiring
    /// <c>EfCorePlanStateStore</c> uses.
    /// </param>
    public EfCoreConversationStore(
        IDbContextFactory<ConversationDbContext> contextFactory,
        TimeProvider timeProvider,
        ILogger<EfCoreConversationStore> logger,
        SchemaInitializer<ConversationDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        _contextFactory = contextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> GetAsync(string conversationId, string callerId, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (entity is null)
            return null;

        // A read has to load the row before it can compare owners, so unlike the mutations there is
        // nothing to fold into a WHERE clause here — filtering by owner would only lose the
        // information needed to answer "forbidden" rather than "not found".
        RequireOwner(conversationId, callerId, entity.UserId);

        var messages = await LoadMessagesAsync(context, conversationId, ct);
        return ConversationEntityMapper.ToRecord(entity, messages);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(userId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entities = await context.Conversations
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        if (entities.Count == 0)
            return [];

        // One query for every message of every listed conversation rather than one query per
        // conversation. ListAsync is a "show me my conversations" call, so the N+1 shape would be
        // paid on a user's first page load.
        var ids = entities.Select(e => e.Id).ToList();

        // A lookup rather than a dictionary: an id with no messages yields an empty sequence
        // instead of a miss, so a conversation created but never spoken in needs no special case.
        var messagesByConversation = (await context.ConversationMessages
                .AsNoTracking()
                .Where(m => ids.Contains(m.ConversationId))
                .OrderBy(m => m.Ordinal)
                .ToListAsync(ct))
            .ToLookup(m => m.ConversationId, ConversationEntityMapper.ToMessage);

        return entities
            .Select(e => ConversationEntityMapper.ToRecord(e, messagesByConversation[e.Id].ToList()))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord> CreateAsync(
        string agentName,
        string userId,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(userId);

        var id = !string.IsNullOrWhiteSpace(conversationId) ? conversationId : Guid.NewGuid().ToString();
        var now = _timeProvider.GetUtcNow();

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        // Replace semantics, matching the file-backed store, where writing the record's file
        // overwrites whatever was there — which makes this the one create path capable of destroying
        // a transcript, and so the one that must not cross an ownership boundary.
        //
        // Read inside the transaction, but note what that does and does not buy: SQLite's default
        // isolation does not hold a write lock from this read, so two creates racing on the same
        // caller-supplied id could both see it absent. What prevents a crossed boundary in that race
        // is that the loser's write blocks and fails rather than interleaving — not this read. The
        // file-backed store closes the window properly, with one lock spanning check and write.
        //
        // The turn lease does NOT close it, despite what an earlier note here said. A lease is taken
        // on a conversation that already exists — it claims the row — so there is nothing for it to
        // hold while a conversation is being created. Closing this properly means an IMMEDIATE
        // transaction, which takes the write lock at BEGIN rather than at first write; that is a
        // change to how every write in this store contends, so it is its own piece of work.
        await ThrowIfOwnedByAnotherAsync(context, id, userId, ct);

        // The cascade takes the messages with it.
        await context.Conversations
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(ct);

        context.Conversations.Add(new ConversationEntity
        {
            Id = id,
            AgentName = agentName,
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogDebug("Created conversation {ConversationId} for user {UserId}.", id, userId);

        return new ConversationRecord(
            Id: id,
            AgentName: agentName,
            UserId: userId,
            CreatedAt: now,
            UpdatedAt: now,
            Messages: []);
    }

    /// <inheritdoc/>
    public async Task AppendMessageAsync(
        string conversationId,
        string callerId,
        ConversationMessage message,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var now = _timeProvider.GetUtcNow();

        // A title is derived from the first user message only. Passing null for every other case
        // lets the UPDATE below express "keep the existing title" as a coalesce, so no read is
        // needed to decide whether one is already set.
        var candidateTitle = message.Role == MessageRole.User
            ? ConversationRecordTitleDerivation.Derive(message.Content)
            : null;

        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var touched = await context.Conversations
            .Where(c => c.Id == conversationId && c.UserId == callerId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.UpdatedAt, now)
                    .SetProperty(c => c.Title, c => c.Title ?? candidateTitle),
                ct);

        // Zero rows means either no such conversation or one belonging to someone else, and the
        // caller is owed different answers for those. Only now is it worth a second query to find out.
        if (touched == 0)
        {
            await ThrowIfOwnedByAnotherAsync(context, conversationId, callerId, ct);
            throw new InvalidOperationException($"Conversation '{conversationId}' does not exist.");
        }

        context.ConversationMessages.Add(ConversationEntityMapper.ToEntity(conversationId, message));

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
        {
            // Message ids are client-supplied, so a replayed or double-submitted turn arrives with an
            // id the conversation already holds. The unique index has to reject it — truncation
            // resolves an id to a cut point, and two rows sharing one id makes that cut arbitrary —
            // but the caller is owed a defined failure rather than whatever the provider threw.
            throw new InvalidOperationException(
                $"Message '{message.Id}' already exists in conversation '{conversationId}'.", ex);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the unique-index violation on
    /// <c>(ConversationId, MessageId)</c> rather than any other write failure.
    /// </summary>
    /// <remarks>
    /// Matched on the <em>extended</em> result code. The primary code, <c>SQLITE_CONSTRAINT</c> (19),
    /// covers every constraint there is — a NOT NULL or foreign-key violation raises it too, and
    /// would then be reported to the caller as a duplicate message id: a misleading diagnosis of a
    /// failure that has nothing to do with one.
    /// </remarks>
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique };

    /// <summary>SQLite's <c>SQLITE_CONSTRAINT_UNIQUE</c> extended result code.</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string conversationId, string callerId, CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Messages go with it through the cascade configured on the foreign key. Owner in the WHERE
        // clause makes this a single statement: the read-check-then-delete it replaces could delete a
        // conversation that changed hands between the two.
        var deleted = await context.Conversations
            .Where(c => c.Id == conversationId && c.UserId == callerId)
            .ExecuteDeleteAsync(ct);

        // Deleting nothing is a no-op for an absent conversation, but a refusal for someone else's.
        if (deleted == 0)
        {
            await ThrowIfOwnedByAnotherAsync(context, conversationId, callerId, ct);
            return false;
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> TruncateFromMessageAsync(
        string conversationId,
        string callerId,
        Guid messageId,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (entity is null)
            return null;

        RequireOwner(conversationId, callerId, entity.UserId);

        var cutoff = await context.ConversationMessages
            .Where(m => m.ConversationId == conversationId && m.MessageId == messageId)
            .Select(m => (long?)m.Ordinal)
            .FirstOrDefaultAsync(ct);

        // Unknown message id: the record is returned untouched, matching the file-backed store.
        if (cutoff is null)
            return ConversationEntityMapper.ToRecord(entity, await LoadMessagesAsync(context, conversationId, ct));

        // Opened only now that there is something to write, so no reader has to work out whether an
        // early return left a transaction hanging: above this line there is no transaction to leave.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var now = _timeProvider.GetUtcNow();

        await context.ConversationMessages
            .Where(m => m.ConversationId == conversationId && m.Ordinal >= cutoff.Value)
            .ExecuteDeleteAsync(ct);

        await context.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, now), ct);

        var remaining = await LoadMessagesAsync(context, conversationId, ct);
        await transaction.CommitAsync(ct);

        // The header was read AsNoTracking before the UPDATE ran, so it still carries the old
        // UpdatedAt. Patching it beats re-reading a row whose new value is already in hand.
        return ConversationEntityMapper.ToRecord(entity, remaining) with { UpdatedAt = now };
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> UpdateSettingsAsync(
        string conversationId,
        string callerId,
        ConversationSettings settings,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var json = JsonSerializer.Serialize(settings, ConversationJson.Options);
        var now = _timeProvider.GetUtcNow();
        return await UpdateHeaderAsync(
            conversationId,
            callerId,
            context => context.Conversations
                .Where(c => c.Id == conversationId && c.UserId == callerId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(c => c.SettingsJson, json)
                        .SetProperty(c => c.UpdatedAt, now),
                    ct),
            ct);
    }

    /// <inheritdoc/>
    public async Task<ConversationRecord?> UpdateTelemetryAsync(
        string conversationId,
        string callerId,
        Guid observabilitySessionId,
        TelemetryAccumulator telemetry,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        var json = JsonSerializer.Serialize(telemetry, ConversationJson.Options);
        var now = _timeProvider.GetUtcNow();
        return await UpdateHeaderAsync(
            conversationId,
            callerId,
            context => context.Conversations
                .Where(c => c.Id == conversationId && c.UserId == callerId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(c => c.ObservabilitySessionId, observabilitySessionId)
                        .SetProperty(c => c.TelemetryJson, json)
                        .SetProperty(c => c.UpdatedAt, now),
                    ct),
            ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        string callerId,
        int maxMessages,
        CancellationToken ct = default)
    {
        ConversationOwnership.RequireCallerId(callerId);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Reading the owner rather than asking whether the row exists: the same single query now
        // answers both questions, so enforcing ownership here costs nothing on the dispatch path.
        var owner = await LoadOwnerAsync(context, conversationId, ct);

        if (owner is null)
            return null;

        RequireOwner(conversationId, callerId, owner);

        // Empty-content messages are excluded before the window is applied: an inline-widget message
        // carries its payload in the widget spec, not in text, so it is not model-relevant. Filtering
        // after the window would let widgets consume slots and then be dropped, silently starving the
        // model of context in a widget-heavy conversation.
        //
        // The take-then-reverse is what keeps this bounded: the database returns at most maxMessages
        // rows instead of the whole transcript, which is the point of dispatching from a window.
        //
        // The clamp is load-bearing, not defensive tidying. EF translates Take to LIMIT, and SQLite
        // reads a negative LIMIT as no limit at all — so passing the value through would answer a
        // request for no messages with the entire transcript, unbounded, straight into the model's
        // context. The file-backed store returns nothing for the same input.
        var tail = await context.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Content != "")
            .OrderByDescending(m => m.Ordinal)
            .Take(Math.Max(0, maxMessages))
            .ToListAsync(ct);

        tail.Reverse();
        return tail.Select(ConversationEntityMapper.ToMessage).ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies a targeted <c>UPDATE</c> to one conversation header and returns the reloaded record,
    /// or <c>null</c> when no such conversation exists.
    /// </summary>
    /// <remarks>
    /// The caller supplies the whole <c>ExecuteUpdateAsync</c> call rather than just its setters:
    /// EF Core translates the <c>SetProperty</c> chain into SQL by reading its expression tree, so
    /// the chain has to be written inline at the call site to stay translatable. What is shared here
    /// is the part worth sharing — the row count as an existence check, and the reload.
    /// </remarks>
    private async Task<ConversationRecord?> UpdateHeaderAsync(
        string conversationId,
        string callerId,
        Func<ConversationDbContext, Task<int>> update,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var touched = await update(context);

        if (touched == 0)
        {
            await ThrowIfOwnedByAnotherAsync(context, conversationId, callerId, ct);
            return null;
        }

        var entity = await context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        // Deleted between the update and the reload — report it the same way as never having existed.
        if (entity is null)
            return null;

        return ConversationEntityMapper.ToRecord(entity, await LoadMessagesAsync(context, conversationId, ct));
    }

    /// <summary>
    /// Throws when <paramref name="conversationId"/> names a conversation owned by someone other than
    /// <paramref name="callerId"/>; returns quietly when it names nothing at all.
    /// </summary>
    /// <remarks>
    /// Called only after an owner-filtered statement matched zero rows, which is why the extra query
    /// is affordable — it runs on the failure path, never on a successful read or write. Returning
    /// quietly when the row is genuinely absent leaves each caller free to say what absence means for
    /// it: a no-op for a delete, a <c>null</c> for an update, an exception for an append.
    /// </remarks>
    private async Task ThrowIfOwnedByAnotherAsync(
        ConversationDbContext context,
        string conversationId,
        string callerId,
        CancellationToken ct)
    {
        var owner = await LoadOwnerAsync(context, conversationId, ct);

        // A row that is present and ours, having matched nothing a moment ago, means it changed
        // underneath this operation. Absent is the honest answer: it is no longer what was written to.
        if (owner is not null)
            RequireOwner(conversationId, callerId, owner);
    }

    /// <summary>
    /// Returns the owner of <paramref name="conversationId"/>, or <c>null</c> if no such conversation
    /// exists.
    /// </summary>
    /// <remarks>
    /// One projection rather than three near-identical ones, so the question "who owns this?" is
    /// asked the same way everywhere it is asked — and so a change to how ownership is stored has one
    /// place to land rather than three that must be spotted.
    /// </remarks>
    private static Task<string?> LoadOwnerAsync(
        ConversationDbContext context,
        string conversationId,
        CancellationToken ct) =>
        context.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => c.UserId)
            .FirstOrDefaultAsync(ct);

    /// <summary>Applies the shared ownership rule. See <see cref="ConversationOwnership"/>.</summary>
    private void RequireOwner(string conversationId, string callerId, string ownerId) =>
        ConversationOwnership.RequireOwner(_logger, conversationId, callerId, ownerId);

    private static async Task<List<ConversationMessage>> LoadMessagesAsync(
        ConversationDbContext context,
        string conversationId,
        CancellationToken ct)
    {
        var rows = await context.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Ordinal)
            .ToListAsync(ct);

        return rows.Select(ConversationEntityMapper.ToMessage).ToList();
    }
}
