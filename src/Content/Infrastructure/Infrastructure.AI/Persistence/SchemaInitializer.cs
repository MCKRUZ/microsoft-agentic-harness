using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Singleton initializer that brings a SQLite subsystem's database up to date at construction time:
/// it creates the database when absent, then adds any columns and indexes the model has gained since
/// an existing one was created. One generic base serves every SQLite subsystem here — prompt usage,
/// the eval dashboard, the planner, governance state and conversations — so none of them
/// re-implements the same few lines.
/// </summary>
/// <typeparam name="TContext">The concrete DbContext type to initialize.</typeparam>
/// <remarks>
/// <para>
/// Resolved once at composition time (typically through a captive factory inside an
/// <c>AddSingleton</c> registration) so the first writer never races a missing-table error.
/// Both steps are idempotent.
/// </para>
/// <para>
/// <strong>Why the second step exists.</strong> <c>EnsureCreated</c> creates a database or does
/// nothing — it never reconciles one that already exists. Without the reconcile pass, a release that
/// adds a property ships a column present in the model, absent from every consumer's existing file,
/// and fatal on first query. The behaviour is measured by <c>SchemaInitializerAddedColumnTests</c>,
/// whose first case is the raw <c>EnsureCreated</c> control.
/// </para>
/// <para>
/// This is <em>not</em> a migration system, and adding columns is the only schema change it will
/// infer — see <see cref="SqliteAdditiveSchemaReconciler"/> for why renames, retypes and drops are
/// deliberately left to real migrations.
/// </para>
/// </remarks>
public class SchemaInitializer<TContext> where TContext : DbContext
{
    /// <summary>
    /// Initializes a new instance, ensuring the underlying database exists and that its tables carry
    /// every column the model declares.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived contexts over the target database.</param>
    public SchemaInitializer(IDbContextFactory<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        using var context = contextFactory.CreateDbContext();
        context.Database.EnsureCreated();
        SqliteAdditiveSchemaReconciler.Reconcile(context);
    }
}
