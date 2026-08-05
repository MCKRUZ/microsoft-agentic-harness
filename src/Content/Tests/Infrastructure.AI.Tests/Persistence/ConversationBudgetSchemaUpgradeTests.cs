using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Infrastructure.AI.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// The upgrade path for a consumer whose <c>conversations.db</c> was created before the budget table
/// existed — every consumer already running the durable conversation store.
/// </summary>
/// <remarks>
/// <para>
/// This is the failure mode the durable budget was built to prevent, arriving by a different route.
/// <c>EnsureCreated</c> no-ops on a database that already exists, so the new table never appears; every
/// statement against it then fails, and <c>SqliteConversationBudgetTracker</c> catches those failures
/// by design so a database blip cannot fail a turn. The result is a ceiling that reports itself
/// enforced and enforces nothing, on precisely the deployments that already have conversations.
/// </para>
/// <para>
/// The first test is the <strong>control</strong>: it measures raw <c>EnsureCreated</c> against a file
/// missing the table, so the second is evidence of a fix rather than an assertion about one. If the
/// control ever starts passing, table creation has moved into EF and the reconciler's table handling is
/// dead weight — delete it rather than keep it.
/// </para>
/// </remarks>
public sealed class ConversationBudgetSchemaUpgradeTests : IDisposable
{
    private const string BudgetTable = "conversation_budgets";

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"conv-budget-upgrade-{Guid.NewGuid():N}.db");

    [Fact]
    public void EnsureCreated_DatabasePredatesTheBudgetTable_DoesNotCreateIt()
    {
        CreateDatabaseWithoutTheBudgetTable();

        using var context = new ConversationDbContext(Options());
        context.Database.EnsureCreated();

        Columns(BudgetTable).Should().BeEmpty(
            "EnsureCreated no-ops on an existing database; if this ever fails the reconciler's table "
            + "creation is redundant");
    }

    [Fact]
    public void SchemaInitializer_DatabasePredatesTheBudgetTable_CreatesItAndKeepsExistingConversations()
    {
        CreateDatabaseWithoutTheBudgetTable();

        _ = new SchemaInitializer<ConversationDbContext>(
            new TestDbContextFactory<ConversationDbContext>(Options()));

        Columns(BudgetTable).Should().BeEquivalentTo("BudgetKey", "ConsumedTokens", "UpdatedAt");

        using var context = new ConversationDbContext(Options());
        context.Conversations.Should().ContainSingle(c => c.Id == "conv-1",
            "creating a missing table must not disturb the tables that were already there");
    }

    /// <summary>
    /// A created table must arrive with its indexes, not just its columns — otherwise the retention
    /// sweep this table exists to allow does a full scan on every consumer who upgraded rather than
    /// started fresh, and nothing says so.
    /// </summary>
    [Fact]
    public void SchemaInitializer_CreatedTable_AlsoGetsItsIndexes()
    {
        CreateDatabaseWithoutTheBudgetTable();

        _ = new SchemaInitializer<ConversationDbContext>(
            new TestDbContextFactory<ConversationDbContext>(Options()));

        Indexes(BudgetTable).Should().Contain("ix_conversation_budgets_updated_at");
    }

    /// <summary>
    /// The created table must be usable through the same model that declared it — a table built with
    /// the wrong column names or types would satisfy a column-name assertion and still fail every real
    /// accrual.
    /// </summary>
    [Fact]
    public void SchemaInitializer_CreatedTable_RoundTripsThroughTheModel()
    {
        CreateDatabaseWithoutTheBudgetTable();

        _ = new SchemaInitializer<ConversationDbContext>(
            new TestDbContextFactory<ConversationDbContext>(Options()));

        var stamp = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        using (var write = new ConversationDbContext(Options()))
        {
            write.ConversationBudgets.Add(new ConversationBudgetEntity
            {
                BudgetKey = "planrun:abc",
                ConsumedTokens = 4_242,
                UpdatedAt = stamp,
            });
            write.SaveChanges();
        }

        using var read = new ConversationDbContext(Options());
        var row = read.ConversationBudgets.Single();
        row.ConsumedTokens.Should().Be(4_242);
        row.UpdatedAt.Should().Be(stamp);
    }

    /// <summary>
    /// Stands in for a consumer's file created by the release before this one: the real schema, with
    /// a real conversation in it, minus the table this change adds.
    /// </summary>
    private void CreateDatabaseWithoutTheBudgetTable()
    {
        using (var context = new ConversationDbContext(Options()))
        {
            context.Database.EnsureCreated();
            context.Conversations.Add(new ConversationEntity
            {
                Id = "conv-1",
                AgentName = "agent",
                UserId = "owner",
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
            context.SaveChanges();
        }

        // Dropping it is how an older file is reproduced faithfully without keeping a copy of the
        // previous model around to rot. Same technique the planner's schema tests use.
        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP TABLE {BudgetTable};";
        drop.ExecuteNonQuery();
    }

    private List<string> Columns(string table)
    {
        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();
        return SqliteSchemaProbe.ColumnsAsync(connection, table).GetAwaiter().GetResult();
    }

    private List<string> Indexes(string table)
    {
        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();
        return SqliteSchemaProbe.IndexNamesAsync(connection, table).GetAwaiter().GetResult();
    }

    private DbContextOptions<ConversationDbContext> Options() =>
        new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite($"DataSource={_databasePath};Pooling=False")
            .Options;

    /// <summary>Releases the temporary database file.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }
}
