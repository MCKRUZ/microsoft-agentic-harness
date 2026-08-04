using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Tests.Support;

/// <summary>
/// Reads what a SQLite database <em>actually</em> has — its columns and indexes — for schema tests
/// that must assert against the file rather than against the EF model that was supposed to produce it.
/// </summary>
/// <remarks>
/// Shared because three schema suites each grew their own copy, and each copy carried its own comment
/// about the quoting rule below. One implementation means one place for that lesson to live.
/// </remarks>
public static class SqliteSchemaProbe
{
    /// <summary>Column names of <paramref name="table"/>, empty when the table does not exist.</summary>
    /// <param name="connection">An open connection to the database under test.</param>
    /// <param name="table">The table to inspect.</param>
    public static Task<List<string>> ColumnsAsync(SqliteConnection connection, string table) =>
        NamesAsync(connection, "pragma_table_info", table);

    /// <summary>Index names on <paramref name="table"/>, empty when it has none.</summary>
    /// <param name="connection">An open connection to the database under test.</param>
    /// <param name="table">The table to inspect.</param>
    public static Task<List<string>> IndexNamesAsync(SqliteConnection connection, string table) =>
        NamesAsync(connection, "pragma_index_list", table);

    /// <summary>
    /// Runs a table-valued pragma and collects its <c>name</c> column.
    /// </summary>
    /// <remarks>
    /// The table name is interpolated rather than bound because a table-valued pragma takes a literal,
    /// not a parameter — and the quoting is load-bearing: <c>pragma_table_info("widgets")</c> parses
    /// the argument as an <em>identifier</em> and SQLite rejects it with
    /// <c>no such column: "widgets"</c>. Single quotes make it a string. The failure looks exactly
    /// like the feature under test being broken, which is how it cost a debugging cycle once already.
    /// Test-only, and the value is a literal from the calling test, never user input.
    /// </remarks>
    private static async Task<List<string>> NamesAsync(
        SqliteConnection connection, string pragma, string table)
    {
        var names = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {pragma}('{table}');";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        return names;
    }
}

/// <summary>
/// Hands out contexts over one set of options, for tests that need an
/// <see cref="IDbContextFactory{TContext}"/> without a service provider.
/// </summary>
/// <typeparam name="TContext">The context type to create.</typeparam>
/// <param name="options">Options every created context is constructed with.</param>
public sealed class TestDbContextFactory<TContext>(DbContextOptions<TContext> options)
    : IDbContextFactory<TContext>
    where TContext : DbContext
{
    /// <inheritdoc />
    public TContext CreateDbContext() => (TContext)Activator.CreateInstance(typeof(TContext), options)!;
}
