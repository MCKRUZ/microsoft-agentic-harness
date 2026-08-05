using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// Pins what <see cref="SchemaInitializer{TContext}"/> does when a model gains a property after its
/// database already exists on disk.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>EnsureCreated</c> creates a database <em>or does nothing</em> — it has no
/// notion of reconciling an existing one. Five subsystems in this assembly build their schema that
/// way (conversations, planner, governance state, prompt usage, eval dashboard), so every one of them
/// would ship a new column that is present in the model, absent from a consumer's existing file, and
/// fatal on first query: <c>SQLite Error 1: 'no such column'</c>.
/// </para>
/// <para>
/// The first test is the <strong>control</strong>: it measures the raw <c>EnsureCreated</c> behaviour
/// so the second test is evidence of a fix rather than an assertion about one. If the control ever
/// starts passing without reconciliation — a future EF Core provider learning to add columns — the
/// reconciler is dead weight and should be deleted, not kept "just in case".
/// </para>
/// </remarks>
public sealed class SchemaInitializerAddedColumnTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"schema-init-{Guid.NewGuid():N}.db");

    [Fact]
    public void EnsureCreated_ModelGainedAColumnAfterTheDatabaseExisted_DoesNotAddIt()
    {
        CreateDatabaseWithTheNarrowModel();

        // Re-running initialization against the WIDER model must be shown to be insufficient on its
        // own — this is the defect the reconciler exists to close.
        using var context = new WideContext(OptionsFor<WideContext>());
        context.Database.EnsureCreated();

        ColumnsOfWidgets().Should().NotContain(
            "Note",
            "EnsureCreated no-ops on an existing database; if this ever fails the reconciler is redundant");
    }

    [Fact]
    public void SchemaInitializer_ModelGainedAColumnAfterTheDatabaseExisted_AddsItAndPreservesRows()
    {
        CreateDatabaseWithTheNarrowModel();

        _ = new SchemaInitializer<WideContext>(new TestDbContextFactory<WideContext>(OptionsFor<WideContext>()));

        ColumnsOfWidgets().Should().Contain("Note");

        using var context = new WideContext(OptionsFor<WideContext>());
        var widget = context.Widgets.Single();
        widget.Id.Should().Be("w1", "reconciliation must add a column, never rebuild the table");
        widget.Note.Should().BeNull("an added column has no value for rows that predate it");
    }

    /// <summary>
    /// A non-nullable added column has no value for rows that predate it, and SQLite refuses
    /// <c>ADD COLUMN … NOT NULL</c> without a default for exactly that reason. The reconciler must
    /// supply one — otherwise every consumer with existing rows fails at startup instead of at the
    /// query that needs the column.
    /// </summary>
    [Fact]
    public void SchemaInitializer_AddedColumnIsNonNullable_BackfillsExistingRowsWithADefault()
    {
        CreateDatabaseWithTheNarrowModel();

        _ = new SchemaInitializer<CounterContext>(new TestDbContextFactory<CounterContext>(OptionsFor<CounterContext>()));

        using var context = new CounterContext(OptionsFor<CounterContext>());
        context.Widgets.Single().Count.Should().Be(
            0,
            "a row written before the column existed must read as the type's zero, not fail to load");
    }

    /// <summary>
    /// A non-nullable column whose declared type matches none of SQLite's affinity keywords must
    /// still reconcile. <c>DATETIME</c> contains no INT, CHAR, CLOB, TEXT, BLOB, REAL, FLOA or DOUB,
    /// so it falls to affinity rule 5 (NUMERIC) — and an earlier draft treated "matched nothing" as
    /// an unknown type and threw. That throw ran inside a DI-resolved constructor, so a consumer who
    /// declared an ordinary <c>DATETIME</c> or <c>BOOLEAN</c> column would have lost host startup
    /// rather than a query.
    /// </summary>
    [Fact]
    public void SchemaInitializer_AddedColumnHasNoAffinityKeyword_ReconcilesInsteadOfThrowing()
    {
        CreateDatabaseWithTheNarrowModel();

        var act = () => new SchemaInitializer<StampedContext>(
            new TestDbContextFactory<StampedContext>(OptionsFor<StampedContext>()));

        act.Should().NotThrow();
        ColumnsOfWidgets().Should().Contain("Stamped");
    }

    /// <summary>
    /// Reconciliation is SQLite-specific and must stand down for any other provider rather than
    /// throw. This runs inside a constructor resolved at DI composition, and a provider with no
    /// relational connection — InMemory, which consumer test suites use widely — throws from
    /// <c>GetDbConnection</c>. Losing this guard costs host startup, not schema evolution.
    /// </summary>
    [Fact]
    public void SchemaInitializer_NonRelationalProvider_StandsDownInsteadOfThrowing()
    {
        var options = new DbContextOptionsBuilder<WideContext>()
            .UseInMemoryDatabase($"schema-init-{Guid.NewGuid():N}")
            .Options;

        var act = () => new SchemaInitializer<WideContext>(new TestDbContextFactory<WideContext>(options));

        act.Should().NotThrow();
    }

    /// <summary>
    /// Reconciliation walks the whole model, so it meets tables the database has never had — a
    /// subsystem whose file predates a second entity entirely. It must create them.
    /// </summary>
    /// <remarks>
    /// This assertion is the reverse of what it was. The earlier version pinned "leave an absent table
    /// alone", reasoning that a hand-built table would lack its indexes and foreign keys. The reasoning
    /// held for hand-built DDL and the conclusion did not: the durable conversation budget was the
    /// first table to ship into existing databases, and on every one of them each statement against it
    /// failed while the tracker's own fault-tolerance swallowed the error — a governance ceiling that
    /// reported itself enforced and enforced nothing. Creation now goes through EF's own migrations
    /// generator, so the objection about indexes and foreign keys no longer applies.
    /// </remarks>
    [Fact]
    public void SchemaInitializer_ModelHasATableTheDatabaseLacks_CreatesIt()
    {
        CreateDatabaseWithTheNarrowModel();

        var act = () => new SchemaInitializer<TwoTableContext>(
            new TestDbContextFactory<TwoTableContext>(OptionsFor<TwoTableContext>()));

        act.Should().NotThrow();
        ColumnsOfWidgets().Should().Contain("Note", "the table that IS present must still be reconciled");
        ColumnsOf("gadgets").Should().Contain("Id", "the table that was absent must now exist");
    }

    /// <summary>
    /// A created table must also get its indexes. EF's create-table operation does not carry them, so
    /// they come from the pass that follows — and that pass only reaches a created table because
    /// creation is tracked separately from "was already present". Without that tracking the table
    /// appears, every column is right, and the indexes are silently missing.
    /// </summary>
    [Fact]
    public void SchemaInitializer_CreatedTable_AlsoGetsItsIndexes()
    {
        CreateDatabaseWithTheNarrowModel();

        _ = new SchemaInitializer<TwoTableContext>(
            new TestDbContextFactory<TwoTableContext>(OptionsFor<TwoTableContext>()));

        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();

        SqliteSchemaProbe.IndexNamesAsync(connection, "gadgets").GetAwaiter().GetResult()
            .Should().Contain("ix_gadgets_label");
    }

    /// <summary>
    /// Builds the database from a model WITHOUT the later column, standing in for a consumer whose
    /// <c>conversations.db</c> was created by an earlier release.
    /// </summary>
    private void CreateDatabaseWithTheNarrowModel()
    {
        using var context = new NarrowContext(OptionsFor<NarrowContext>());
        context.Database.EnsureCreated();
        context.Widgets.Add(new NarrowWidget { Id = "w1" });
        context.SaveChanges();
    }

    private List<string> ColumnsOfWidgets() => ColumnsOf("widgets");

    private List<string> ColumnsOf(string table)
    {
        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();
        return SqliteSchemaProbe.ColumnsAsync(connection, table).GetAwaiter().GetResult();
    }

    private DbContextOptions<T> OptionsFor<T>() where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseSqlite($"DataSource={_databasePath};Pooling=False")
            .Options;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private sealed class NarrowWidget
    {
        public required string Id { get; set; }
    }

    private sealed class WideWidget
    {
        public required string Id { get; set; }

        /// <summary>The property added after the database already existed.</summary>
        public string? Note { get; set; }
    }

    private sealed class CounterWidget
    {
        public required string Id { get; set; }

        /// <summary>A non-nullable property added after the database already existed.</summary>
        public long Count { get; set; }
    }

    private sealed class Gadget
    {
        public required string Id { get; set; }

        /// <summary>Indexed, so a created table can be checked for more than its columns.</summary>
        public string? Label { get; set; }
    }

    private sealed class StampedWidget
    {
        public required string Id { get; set; }

        /// <summary>Non-nullable, and declared as a type matching no SQLite affinity keyword.</summary>
        public DateTime Stamped { get; set; }
    }

    private sealed class NarrowContext(DbContextOptions<NarrowContext> options) : DbContext(options)
    {
        public DbSet<NarrowWidget> Widgets => Set<NarrowWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<NarrowWidget>().ToTable("widgets").HasKey(e => e.Id);
    }

    private sealed class CounterContext(DbContextOptions<CounterContext> options) : DbContext(options)
    {
        public DbSet<CounterWidget> Widgets => Set<CounterWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CounterWidget>().ToTable("widgets").HasKey(e => e.Id);
    }

    private sealed class StampedContext(DbContextOptions<StampedContext> options) : DbContext(options)
    {
        public DbSet<StampedWidget> Widgets => Set<StampedWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var widget = modelBuilder.Entity<StampedWidget>();
            widget.ToTable("widgets").HasKey(e => e.Id);

            // Stated outright rather than left to the provider: EF's SQLite provider would map
            // DateTime to TEXT, which DOES carry an affinity keyword and would not exercise the
            // fallback this test exists for.
            widget.Property(e => e.Stamped).HasColumnType("DATETIME");
        }
    }

    /// <summary>A model whose second table has no counterpart in the existing database.</summary>
    private sealed class TwoTableContext(DbContextOptions<TwoTableContext> options) : DbContext(options)
    {
        public DbSet<WideWidget> Widgets => Set<WideWidget>();

        public DbSet<Gadget> Gadgets => Set<Gadget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WideWidget>().ToTable("widgets").HasKey(e => e.Id);

            var gadget = modelBuilder.Entity<Gadget>();
            gadget.ToTable("gadgets").HasKey(e => e.Id);
            gadget.HasIndex(e => e.Label).HasDatabaseName("ix_gadgets_label");
        }
    }

    private sealed class WideContext(DbContextOptions<WideContext> options) : DbContext(options)
    {
        public DbSet<WideWidget> Widgets => Set<WideWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<WideWidget>().ToTable("widgets").HasKey(e => e.Id);
    }
}
