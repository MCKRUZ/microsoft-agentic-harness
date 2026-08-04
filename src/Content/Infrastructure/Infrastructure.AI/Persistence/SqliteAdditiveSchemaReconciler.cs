using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Data.Common;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Adds the columns and indexes a model has gained since its SQLite database was created. Purely
/// additive: it issues <c>ALTER TABLE … ADD COLUMN</c> and <c>CREATE INDEX IF NOT EXISTS</c> and
/// nothing else — never a drop, rename, retype, or table rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is needed.</strong> Every SQLite subsystem here builds its schema with
/// <c>EnsureCreated</c>, which creates a database <em>or does nothing</em>. It has no notion of
/// reconciling one that already exists, so a release that adds a property ships a column that is
/// present in the model, absent from every consumer's existing file, and fatal on first query
/// (<c>SQLite Error 1: 'no such column'</c>). That is measured, not assumed — see
/// <c>SchemaInitializerAddedColumnTests</c>, whose first case is the raw <c>EnsureCreated</c> control.
/// </para>
/// <para>
/// <strong>What it deliberately does not do.</strong> Only additions are safe to infer. A rename is
/// indistinguishable from "drop one column, add another" at this level, and acting on that guess
/// would silently discard a column of data; a type change needs SQLite's twelve-step table rebuild.
/// Both remain the job of real migrations. This closes the one gap that is unambiguous and, being
/// additive, cannot destroy anything: an unrecognised existing column is left exactly where it is.
/// </para>
/// <para>
/// <strong>Non-nullable columns.</strong> SQLite refuses <c>ADD COLUMN … NOT NULL</c> without a
/// default, and rightly so — existing rows would have no value. A literal default is supplied for
/// the types where one is unambiguous (zero for numerics, empty for text/blob). A non-nullable
/// column of any other type throws rather than guessing, because a wrong default is data a consumer
/// cannot tell from real data.
/// </para>
/// </remarks>
public static class SqliteAdditiveSchemaReconciler
{
    /// <summary>
    /// Brings every table in <paramref name="context"/>'s model up to date with the columns and
    /// indexes added since the database was created. A no-op on a database that already matches, and
    /// on one that <c>EnsureCreated</c> has just built from scratch.
    /// </summary>
    /// <param name="context">The context whose model and database should be reconciled.</param>
    /// <returns>
    /// The column names added, qualified as <c>table.column</c>; empty when none were. Indexes are
    /// not reported: <c>CREATE INDEX IF NOT EXISTS</c> does not say whether it did anything, and
    /// asking afterwards would not distinguish an index this call made from one already there.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// A missing column is non-nullable and has no unambiguous default for its storage type.
    /// </exception>
    public static IReadOnlyList<string> Reconcile(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            connection.Open();

        try
        {
            var added = new List<string>();

            foreach (var table in ModelTables(context))
            {
                var existing = ExistingColumns(connection, table.Name);

                // An empty result means the table is not there at all. EnsureCreated owns creation;
                // inventing one here would build it without indexes or foreign keys.
                if (existing.Count == 0)
                    continue;

                foreach (var column in table.Columns)
                {
                    if (existing.Contains(column.Name))
                        continue;

                    AddColumn(connection, table.Name, column);
                    added.Add($"{table.Name}.{column.Name}");
                }

                // After the columns, because an index over a just-added column cannot be created
                // before it exists — which is the exact case a scope-filtering index is added for.
                foreach (var index in table.Indexes)
                    CreateIndexIfMissing(connection, table.Name, index);
            }

            return added;
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    /// <summary>
    /// Projects the model into tables and their columns, collapsing entity types that share a table
    /// so a shared table is examined once with the union of its columns.
    /// </summary>
    private static IEnumerable<TableSpec> ModelTables(DbContext context)
    {
        var columnsByTable = new Dictionary<string, Dictionary<string, ColumnSpec>>(StringComparer.OrdinalIgnoreCase);
        var indexesByTable = new Dictionary<string, Dictionary<string, IndexSpec>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            // Null for a keyless type mapped to a view or a raw SQL query — nothing to alter.
            if (string.IsNullOrEmpty(tableName))
                continue;

            var identifier = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

            if (!columnsByTable.TryGetValue(tableName, out var columns))
            {
                columns = new Dictionary<string, ColumnSpec>(StringComparer.OrdinalIgnoreCase);
                columnsByTable[tableName] = columns;
                indexesByTable[tableName] = new Dictionary<string, IndexSpec>(StringComparer.OrdinalIgnoreCase);
            }

            var indexes = indexesByTable[tableName];

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(identifier);
                if (string.IsNullOrEmpty(columnName) || columns.ContainsKey(columnName))
                    continue;

                columns[columnName] = new ColumnSpec(
                    columnName,
                    property.GetColumnType(),
                    property.IsNullable);
            }

            foreach (var index in entityType.GetIndexes())
            {
                var indexName = index.GetDatabaseName(identifier);
                if (string.IsNullOrEmpty(indexName) || indexes.ContainsKey(indexName))
                    continue;

                var indexColumns = index.Properties
                    .Select(p => p.GetColumnName(identifier))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Select(c => c!)
                    .ToList();

                if (indexColumns.Count != index.Properties.Count)
                    continue;

                indexes[indexName] = new IndexSpec(indexName, indexColumns, index.IsUnique);
            }
        }

        foreach (var (name, columns) in columnsByTable)
            yield return new TableSpec(name, columns.Values.ToList(), indexesByTable[name].Values.ToList());
    }

    /// <summary>Reads the column names SQLite currently has for a table; empty when it has no such table.</summary>
    private static HashSet<string> ExistingColumns(DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }

    private static void AddColumn(DbConnection connection, string table, ColumnSpec column)
    {
        using var command = connection.CreateCommand();

        // Identifiers cannot be parameterised in DDL. Both come from the compiled EF model — type
        // names and property/column names the application itself declares — never from user input,
        // and they are quoted so an identifier needing escaping still round-trips.
        command.CommandText =
            $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(column.Name)} {column.StoreType}{NullabilityClause(column)};";

        command.ExecuteNonQuery();
    }

    private static string NullabilityClause(ColumnSpec column)
    {
        if (column.IsNullable)
            return string.Empty;

        return $" NOT NULL DEFAULT {DefaultLiteralFor(column)}";
    }

    /// <summary>
    /// The literal used to backfill existing rows for a non-nullable added column. Derived from
    /// SQLite's type affinity rules, which are prefix-based rather than an exact name match.
    /// </summary>
    private static string DefaultLiteralFor(ColumnSpec column)
    {
        var type = column.StoreType.ToUpperInvariant();

        if (type.Contains("INT", StringComparison.Ordinal))
            return "0";

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal) ||
            type.Contains("NUMERIC", StringComparison.Ordinal) ||
            type.Contains("DECIMAL", StringComparison.Ordinal))
            return "0";

        if (type.Contains("CHAR", StringComparison.Ordinal) ||
            type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal))
            return "''";

        if (type.Contains("BLOB", StringComparison.Ordinal))
            return "x''";

        throw new NotSupportedException(
            $"Cannot add non-nullable column '{column.Name}' of type '{column.StoreType}': no unambiguous " +
            "default exists for existing rows. Make the property nullable, or add the column with a real migration.");
    }

    /// <summary>
    /// Creates a model index the database does not have. <c>IF NOT EXISTS</c> carries the idempotence,
    /// so an index already present — under this name, however it was originally made — is left alone.
    /// </summary>
    /// <remarks>
    /// A <em>unique</em> index over data that already violates it will throw here. That is deliberate:
    /// the alternative is a uniqueness constraint the model advertises and the database does not
    /// enforce, which is the silently-inert-control failure this repo keeps paying for. Failing at
    /// startup names the problem while the data is still there to fix.
    /// </remarks>
    private static void CreateIndexIfMissing(DbConnection connection, string table, IndexSpec index)
    {
        var columns = string.Join(", ", index.Columns.Select(Quote));
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE {unique}INDEX IF NOT EXISTS {Quote(index.Name)} ON {Quote(table)} ({columns});";

        command.ExecuteNonQuery();
    }

    /// <summary>Quotes an identifier for SQLite, escaping any embedded double quote.</summary>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private readonly record struct ColumnSpec(string Name, string StoreType, bool IsNullable);

    private readonly record struct IndexSpec(string Name, IReadOnlyList<string> Columns, bool IsUnique);

    private readonly record struct TableSpec(
        string Name,
        IReadOnlyList<ColumnSpec> Columns,
        IReadOnlyList<IndexSpec> Indexes);
}
