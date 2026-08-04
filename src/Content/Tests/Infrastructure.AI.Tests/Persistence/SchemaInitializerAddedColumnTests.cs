using FluentAssertions;
using Infrastructure.AI.Persistence;
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

        _ = new SchemaInitializer<WideContext>(new Factory<WideContext>(OptionsFor<WideContext>()));

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

        _ = new SchemaInitializer<CounterContext>(new Factory<CounterContext>(OptionsFor<CounterContext>()));

        using var context = new CounterContext(OptionsFor<CounterContext>());
        context.Widgets.Single().Count.Should().Be(
            0,
            "a row written before the column existed must read as the type's zero, not fail to load");
    }

    /// <summary>
    /// Reconciliation walks the whole model, so it meets tables the database has never had — a
    /// subsystem whose file predates a second entity entirely. Creating one here would build it
    /// without its indexes or foreign keys, so an absent table is left for EnsureCreated to own.
    /// </summary>
    [Fact]
    public void SchemaInitializer_ModelHasATableTheDatabaseLacks_LeavesItAloneWithoutThrowing()
    {
        CreateDatabaseWithTheNarrowModel();

        var act = () => new SchemaInitializer<TwoTableContext>(
            new Factory<TwoTableContext>(OptionsFor<TwoTableContext>()));

        act.Should().NotThrow();
        ColumnsOfWidgets().Should().Contain("Note", "the table that IS present must still be reconciled");
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

    private List<string> ColumnsOfWidgets()
    {
        using var connection = new SqliteConnection($"DataSource={_databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('widgets');";

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
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

    private sealed class Factory<T>(DbContextOptions<T> options) : IDbContextFactory<T>
        where T : DbContext
    {
        public T CreateDbContext() => (T)Activator.CreateInstance(typeof(T), options)!;
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

    /// <summary>A model whose second table has no counterpart in the existing database.</summary>
    private sealed class TwoTableContext(DbContextOptions<TwoTableContext> options) : DbContext(options)
    {
        public DbSet<WideWidget> Widgets => Set<WideWidget>();

        public DbSet<Gadget> Gadgets => Set<Gadget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WideWidget>().ToTable("widgets").HasKey(e => e.Id);
            modelBuilder.Entity<Gadget>().ToTable("gadgets").HasKey(e => e.Id);
        }
    }

    private sealed class WideContext(DbContextOptions<WideContext> options) : DbContext(options)
    {
        public DbSet<WideWidget> Widgets => Set<WideWidget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<WideWidget>().ToTable("widgets").HasKey(e => e.Id);
    }
}
