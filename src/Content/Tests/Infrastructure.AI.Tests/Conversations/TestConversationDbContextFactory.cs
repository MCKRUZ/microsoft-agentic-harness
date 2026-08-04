using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// Hands out contexts against one on-disk conversation database, for the tests that need several
/// independent components pointed at the same file.
/// </summary>
/// <remarks>
/// <para>
/// A <em>file</em>, never <c>:memory:</c>. An in-memory SQLite database lives inside a single
/// connection, which would serialise every writer by itself and quietly make the cross-instance tests
/// — the store's concurrent appends, the lease's two-host handover — prove nothing.
/// </para>
/// <para>
/// <c>Pooling=False</c> so that the file is closed when the last context is, and the temporary
/// directory can be deleted at the end of a test rather than being held open by a pooled connection.
/// </para>
/// </remarks>
/// <param name="databasePath">Full path to the database file. Need not exist yet.</param>
internal sealed class TestConversationDbContextFactory(string databasePath)
    : IDbContextFactory<ConversationDbContext>
{
    private readonly DbContextOptions<ConversationDbContext> _options =
        new DbContextOptionsBuilder<ConversationDbContext>()
            .UseSqlite($"DataSource={databasePath};Pooling=False")
            .Options;

    /// <inheritdoc />
    public ConversationDbContext CreateDbContext() => new(_options);
}
