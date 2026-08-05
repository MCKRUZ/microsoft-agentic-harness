using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Adds the tables, columns and indexes a model has gained since its SQLite database was created.
/// Purely additive: it issues <c>CREATE TABLE</c> for a table the database lacks entirely,
/// <c>ALTER TABLE … ADD COLUMN</c>, and <c>CREATE INDEX IF NOT EXISTS</c> — never a drop, rename,
/// retype, or table rebuild.
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
/// default, and rightly so — existing rows would have no value. The zero value of the column's
/// type affinity is supplied: zero for numerics, empty for text and blob. Every SQLite type resolves
/// to one of five affinities, so there is no "unknown type" case to fail on.
/// </para>
/// </remarks>
public static class SqliteAdditiveSchemaReconciler
{
    /// <summary>
    /// Brings <paramref name="context"/>'s database up to date with the tables, columns and indexes
    /// its model has gained since that database was created. A no-op on a database that already
    /// matches, and on one that <c>EnsureCreated</c> has just built from scratch.
    /// </summary>
    /// <param name="context">The context whose model and database should be reconciled.</param>
    public static void Reconcile(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Everything below is SQLite-specific — pragma_table_info, the affinity defaults, ALTER TABLE
        // ADD COLUMN's exact syntax. Both hand-rolled predecessors carried this same guard, and
        // dropping it would matter more than it looks: on a provider with no relational connection
        // (InMemory, used widely in consumer tests) GetDbConnection throws, and this runs inside a
        // constructor resolved at DI composition — so a consumer swapping the provider would lose
        // host startup rather than schema evolution. A non-SQLite database has nothing to reconcile
        // anyway: whatever created it built it from the current model.
        if (!context.Database.IsSqlite())
            return;

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            connection.Open();

        try
        {
            var tables = ModelTables(context);

            // One probe per table for the whole pass. Both the creation step and the column step need
            // to know which columns a table currently has, and asking twice doubles the pragma queries
            // for no new information.
            var columnsByTable = tables.ToDictionary(
                table => table.Name,
                table => ExistingColumns(connection, table.Name),
                StringComparer.OrdinalIgnoreCase);

            // Whole tables first, and in one pass, because a table added at the same time as another
            // may carry a foreign key to it — creating them one at a time in model order would fail on
            // whichever came first. EF's own generator orders them for exactly this reason.
            var created = CreateMissingTables(context, connection, columnsByTable);

            foreach (var table in tables)
            {
                var justCreated = created.Contains(table.Name);

                // Neither there before nor creatable now — a table EF's differ did not emit. Altering
                // or indexing it would fail against nothing.
                if (!justCreated && columnsByTable[table.Name].Count == 0)
                    continue;

                // A table created just above already has every column the model declares, because the
                // same model produced both. Only an older table can be missing one.
                if (!justCreated)
                {
                    var existing = columnsByTable[table.Name];

                    foreach (var column in table.Columns)
                    {
                        if (existing.Contains(column.Name))
                            continue;

                        AddColumn(connection, table.Name, column);
                    }
                }

                // After the columns, because an index over a just-added column cannot be created
                // before it exists — which is the exact case a scope-filtering index is added for.
                // A created table reaches here too: a create-table operation carries no indexes.
                foreach (var index in table.Indexes)
                    CreateIndexIfMissing(connection, table.Name, index);
            }
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    /// <summary>
    /// Creates any table the model declares and the database does not have, using EF Core's own
    /// migrations generator so the result is byte-for-byte what <c>EnsureCreated</c> would have built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is needed, separately from added columns.</strong> A release that adds a whole
    /// <em>entity</em> is in the same position as one that adds a property: <c>EnsureCreated</c> no-ops
    /// on an existing file, so the table is present in the model and absent from every consumer's
    /// database. An earlier revision deliberately skipped this case, reasoning that a hand-built table
    /// would lack its indexes and foreign keys. That reasoning was right about hand-building and wrong
    /// about the conclusion — the durable conversation budget was the first table to ship this way, and
    /// on an upgraded database every statement against it failed while the tracker's own
    /// fault-tolerance swallowed the error, leaving a governance ceiling that reported itself enforced
    /// and enforced nothing.
    /// </para>
    /// <para>
    /// <strong>Why EF's generator rather than hand-built DDL.</strong> Asking
    /// <see cref="IMigrationsModelDiffer"/> for the difference between "nothing" and the current model
    /// yields the same <see cref="CreateTableOperation"/> set EF uses to create a database from
    /// scratch, complete with primary keys, foreign keys, delete behaviours and store types. Only the
    /// operations for missing tables are executed. Nothing here needs to know how SQLite spells a
    /// composite key, and a future model feature is handled by EF rather than by this file.
    /// </para>
    /// <para>
    /// Indexes are deliberately left to the pass that follows: a create-table operation does not carry
    /// them, and that pass is already idempotent.
    /// </para>
    /// </remarks>
    /// <param name="context">The context whose model supplies the table definitions.</param>
    /// <param name="connection">An open connection to the database being reconciled.</param>
    /// <param name="columnsByTable">
    /// Each model table's current columns; an empty entry is how a wholly absent table presents.
    /// </param>
    /// <returns>
    /// The names of the tables actually created, so the caller can tell them from tables that were
    /// already there — a created table needs no column reconciliation, and one that could not be
    /// created must not be altered or indexed at all.
    /// </returns>
    private static HashSet<string> CreateMissingTables(
        DbContext context,
        DbConnection connection,
        IReadOnlyDictionary<string, HashSet<string>> columnsByTable)
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var missing = columnsByTable
            .Where(entry => entry.Value.Count == 0)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (missing.Count == 0)
            return created;

        var differ = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var model = context.GetService<IDesignTimeModel>().Model;

        var creations = differ
            .GetDifferences(null, model.GetRelationalModel())
            .OfType<CreateTableOperation>()
            .Where(operation => missing.Contains(operation.Name))
            .ToList();

        if (creations.Count == 0)
            return created;

        foreach (var command in generator.Generate(creations, model))
        {
            using var statement = connection.CreateCommand();
            statement.CommandText = command.CommandText;
            statement.ExecuteNonQuery();
        }

        foreach (var operation in creations)
            created.Add(operation.Name);

        return created;
    }

    /// <summary>
    /// Projects the model into tables and their columns, collapsing entity types that share a table
    /// so a shared table is examined once with the union of its columns.
    /// </summary>
    private static List<TableSpec> ModelTables(DbContext context)
    {
        var byTable = new Dictionary<string, TableAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            // Null for a keyless type mapped to a view or a raw SQL query — nothing to alter.
            if (string.IsNullOrEmpty(tableName))
                continue;

            var identifier = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

            if (!byTable.TryGetValue(tableName, out var table))
            {
                table = new TableAccumulator();
                byTable[tableName] = table;
            }

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(identifier);
                if (string.IsNullOrEmpty(columnName) || table.Columns.ContainsKey(columnName))
                    continue;

                // The store-object overloads, not the parameterless ones. They differ exactly where a
                // table is shared: under TPH a derived type's non-nullable property MUST be nullable
                // in the store, because rows of the sibling types have no value for it.
                // `property.IsNullable` reports the CLR-side answer, so reconciliation would emit
                // NOT NULL where EnsureCreated emits nullable — a reconciled database that differs
                // from a freshly created one. No model here shares a table today; a consumer's will.
                table.Columns[columnName] = new ColumnSpec(
                    columnName,
                    property.GetColumnType(identifier),
                    property.IsColumnNullable(identifier));
            }

            foreach (var index in entityType.GetIndexes())
            {
                var indexName = index.GetDatabaseName(identifier);
                if (string.IsNullOrEmpty(indexName) || table.Indexes.ContainsKey(indexName))
                    continue;

                // A non-null database name means the index IS mapped to this store object, so every
                // one of its properties has a column here — no arity guard is reachable.
                var indexColumns = index.Properties
                    .Select(p => p.GetColumnName(identifier)!)
                    .ToList();

                table.Indexes[indexName] = new IndexSpec(indexName, indexColumns, index.IsUnique);
            }
        }

        return byTable
            .Select(entry => new TableSpec(
                entry.Key,
                entry.Value.Columns.Values.ToList(),
                entry.Value.Indexes.Values.ToList()))
            .ToList();
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

        // Identifiers cannot be parameterised in DDL. All three interpolated values come from the
        // compiled EF model — table and column names the application declares, and a store type EF
        // derives or the model states outright — never from configuration or user input. The two
        // identifiers are quoted so one needing escaping still round-trips; the store type is
        // deliberately NOT quoted, because it is a type declaration rather than an identifier and
        // quoting it would produce a column typed `"TEXT"` instead of TEXT.
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
    /// The literal used to backfill existing rows for a non-nullable added column: the zero value of
    /// the column's SQLite type affinity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The five branches are SQLite's own affinity determination (datatype documentation, §3.1), kept
    /// in its documented order and labelled, because the rules are overlapping substring matches whose
    /// order is the tie-break — <c>FLOATING POINT</c> gets INTEGER affinity, not REAL, because it
    /// contains <c>INT</c> inside "POINT" and the INT rule is first.
    /// </para>
    /// <para>
    /// Only two of those branches change <em>this</em> function's answer, though: INTEGER, REAL and
    /// NUMERIC all zero to <c>0</c>, so the order matters here solely for TEXT and BLOB. The full
    /// shape is kept anyway — it is what makes the mapping checkable against the specification, and
    /// it is where a per-affinity default would go if one is ever needed. Do not reorder it to read
    /// better; it would stop matching the spec it is derived from.
    /// </para>
    /// <para>
    /// Rule 5 is a <em>fallback</em>, not a keyword match: a type name matching nothing above is
    /// NUMERIC, which is why an unrecognised type is not an error. An earlier draft threw for anything
    /// unmatched, which made ordinary declared types like <c>DATETIME</c> and <c>BOOLEAN</c> — neither
    /// of which contains any keyword above — fail host startup.
    /// </para>
    /// </remarks>
    private static string DefaultLiteralFor(ColumnSpec column)
    {
        var type = column.StoreType.ToUpperInvariant();

        // Rule 1 — INTEGER.
        if (type.Contains("INT", StringComparison.Ordinal))
            return "0";

        // Rule 2 — TEXT.
        if (type.Contains("CHAR", StringComparison.Ordinal) ||
            type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal))
            return "''";

        // Rule 3 — BLOB, which is also where a column declared with no type at all lands.
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal))
            return "x''";

        // Rule 4 — REAL.
        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
            return "0";

        // Rule 5 — NUMERIC, the fallback for everything else.
        return "0";
    }

    /// <summary>
    /// Creates a model index the database does not have. <c>IF NOT EXISTS</c> carries the idempotence,
    /// so an index already present — under this name, however it was originally made — is left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <em>unique</em> index over data that already violates it will throw here. That is deliberate:
    /// the alternative is a uniqueness constraint the model advertises and the database does not
    /// enforce, which is the silently-inert-control failure this repo keeps paying for. Failing at
    /// startup names the problem while the data is still there to fix.
    /// </para>
    /// <para>
    /// Worth knowing before adding one: this runs for every SQLite subsystem, including the three that
    /// never asked for schema evolution, and it runs inside a constructor resolved at DI composition —
    /// so the throw costs host startup and arrives wrapped in a DI resolution failure. Nothing can hit
    /// it today (every unique index here shipped with its own table, so no existing database lacks
    /// one). Adding a unique index to a table that already has rows is the case that would, and it
    /// wants a real migration that de-duplicates first.
    /// </para>
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

    /// <summary>Mutable while a table's entity types are being folded together; see <see cref="TableSpec"/>.</summary>
    private sealed class TableAccumulator
    {
        public Dictionary<string, ColumnSpec> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, IndexSpec> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct ColumnSpec(string Name, string StoreType, bool IsNullable);

    private readonly record struct IndexSpec(string Name, IReadOnlyList<string> Columns, bool IsUnique);

    private readonly record struct TableSpec(
        string Name,
        IReadOnlyList<ColumnSpec> Columns,
        IReadOnlyList<IndexSpec> Indexes);
}
