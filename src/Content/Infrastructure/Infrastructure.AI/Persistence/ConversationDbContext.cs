using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// EF Core DbContext for durable conversation transcripts. Targets SQLite, stores each message as
/// its own row, and keeps <see cref="DateTimeOffset"/> columns as UTC ticks so they sort correctly.
/// </summary>
/// <remarks>
/// <para>
/// The model is configured inline here rather than through <c>IEntityTypeConfiguration</c> classes,
/// matching <c>PromptUsageDbContext</c> and <see cref="GovernanceStateDbContext"/>. This is
/// load-bearing, not stylistic:
/// <see cref="PlannerDbContext"/> builds its model with
/// <c>ApplyConfigurationsFromAssembly(typeof(PlannerDbContext).Assembly)</c>, which picks up
/// <em>every</em> configuration class in Infrastructure.AI. A conversation configuration class would
/// therefore be applied to the planner's model too, and <c>EnsureCreated</c> would quietly build
/// conversation tables inside <c>planner.db</c>.
/// </para>
/// </remarks>
public sealed class ConversationDbContext : DbContext
{
    /// <summary>Conversation headers.</summary>
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();

    /// <summary>Messages, one row each, ordered within a conversation by ordinal.</summary>
    public DbSet<ConversationMessageEntity> ConversationMessages => Set<ConversationMessageEntity>();

    /// <summary>
    /// Cumulative token spend per budget key. Unrelated to <see cref="Conversations"/> by design — see
    /// <see cref="ConversationBudgetEntity"/>.
    /// </summary>
    public DbSet<ConversationBudgetEntity> ConversationBudgets => Set<ConversationBudgetEntity>();

    /// <summary>Initializes a new context with the supplied options.</summary>
    /// <param name="options">Provider and connection options.</param>
    public ConversationDbContext(DbContextOptions<ConversationDbContext> options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var conversation = modelBuilder.Entity<ConversationEntity>();
        conversation.ToTable("conversations");
        conversation.HasKey(e => e.Id);
        conversation.Property(e => e.Id).HasMaxLength(200).ValueGeneratedNever();
        conversation.Property(e => e.AgentName).IsRequired().HasMaxLength(200);
        conversation.Property(e => e.UserId).IsRequired().HasMaxLength(200);
        conversation.Property(e => e.Title).HasMaxLength(200);
        conversation.Property(e => e.CreatedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);
        conversation.Property(e => e.UpdatedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        conversation.Property(e => e.LeaseOwner).HasMaxLength(200);

        // Same UTC-ticks conversion as the other timestamps, and load-bearing for more than sorting
        // here: claiming a lease compares this column against the current instant inside the WHERE
        // clause, and EF's default DateTimeOffset storage is a text+offset tuple that does not
        // compare as an instant.
        conversation.Property(e => e.LeaseExpiresAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        // Serves ListAsync, the only query that filters by anything other than the primary key.
        // Ordered by recency because that is how every caller presents a conversation list.
        conversation.HasIndex(e => new { e.UserId, e.UpdatedAt })
            .HasDatabaseName("ix_conversations_user_updated_at");

        var message = modelBuilder.Entity<ConversationMessageEntity>();
        message.ToTable("conversation_messages");
        message.HasKey(e => e.Ordinal);
        message.Property(e => e.Ordinal).ValueGeneratedOnAdd();
        message.Property(e => e.ConversationId).IsRequired().HasMaxLength(200);
        // Stored as the enum name. Declared once here rather than converted by hand on each side of
        // the mapping, so the column and the CLR property cannot drift apart.
        message.Property(e => e.Role)
            .HasConversion<string>()
            .IsRequired()
            .HasMaxLength(32);
        message.Property(e => e.Content).IsRequired();
        message.Property(e => e.Timestamp).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        // Cascade so DeleteAsync stays one statement and can never leave orphaned messages behind.
        message.HasOne(e => e.Conversation)
            .WithMany(e => e.Messages)
            .HasForeignKey(e => e.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reads are always "this conversation's messages in append order".
        message.HasIndex(e => new { e.ConversationId, e.Ordinal })
            .HasDatabaseName("ix_conversation_messages_conversation_ordinal");

        // TruncateFromMessageAsync resolves a caller-supplied message id to its ordinal. Unique
        // within a conversation so that lookup cannot become ambiguous — a duplicate id would make
        // truncation cut at an arbitrary one of the matches.
        message.HasIndex(e => new { e.ConversationId, e.MessageId })
            .IsUnique()
            .HasDatabaseName("ux_conversation_messages_conversation_message_id");

        var budget = modelBuilder.Entity<ConversationBudgetEntity>();
        budget.ToTable("conversation_budgets");
        // The key is the identity: every read and every accrual addresses exactly one key, so the
        // primary-key index is the only one this table needs for its hot path. No relationship to
        // `conversations` is declared — two of the four callers have no row there.
        budget.HasKey(e => e.BudgetKey);
        budget.Property(e => e.BudgetKey).HasMaxLength(200).ValueGeneratedNever();
        budget.Property(e => e.ConsumedTokens).IsRequired();
        budget.Property(e => e.UpdatedAt).HasConversion(SqliteValueConverters.DateTimeOffsetAsUtcTicks);

        // Still no index on UpdatedAt, now that a reader for one exists. The retention sweep
        // (SqliteConversationBudgetTracker.SweepAbandonedAsync) filters on it, so an index was the
        // obvious addition — and it would cost more than it saves. UpdatedAt is rewritten by every
        // accrual, which is the per-turn hot path, so the index would be maintained on every turn to
        // speed a query that runs four times a day. It is also poorly selective for that query: in
        // steady state most surviving rows ARE older than the grace period, and their conversations
        // still exist, so the age predicate excludes almost nothing and the correlated NOT EXISTS —
        // which hits `conversations` by primary key — does the real work.
        //
        // Revisit if this table ever grows far beyond one row per conversation, which would mean the
        // sweep has stopped working and the index is not the problem to fix (issue #253).
    }
}
