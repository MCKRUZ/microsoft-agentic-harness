using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config;
using Domain.Common.Config.AI.Conversations;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the durable conversation transcript store, backed by whichever provider
    /// <see cref="ConversationsConfig.Provider"/> selects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This lives in Infrastructure rather than in a host's own composition because the transcript
    /// store is shared: the interactive AgentHub host and the Execution API both read and write the
    /// same conversations. While the registration belonged to <c>Presentation.AgentHub</c> the
    /// harness had the capability exactly once, reachable only from its interactive entry point.
    /// </para>
    /// <para>
    /// <strong>Singleton is load-bearing for the file-backed provider.</strong>
    /// <see cref="FileSystemConversationStore"/> serialises all of its file I/O behind one
    /// <see cref="SemaphoreSlim"/>; a scoped or transient registration would hand out several stores
    /// with several semaphores and lose that serialisation.
    /// <see cref="EfCoreConversationStore"/> holds no such state — it takes a context factory and
    /// creates a short-lived context per operation — but is registered the same way so the lifetime
    /// does not change under the host when the provider does.
    /// </para>
    /// </remarks>
    private static void RegisterConversationStore(IServiceCollection services, AppConfig appConfig)
    {
        // Hand the config section straight through rather than copying its properties across. The
        // source and target are the same type, so a property-by-property projection would only add a
        // place to forget: a setting added to ConversationsConfig later would silently keep its
        // default with every test still green. Same idiom as the ModelRouting/KnowledgeBridge
        // registrations in DependencyInjection.cs.
        services.AddSingleton(Options.Create(appConfig.AI.Conversations));

        // Both stores stamp CreatedAt/UpdatedAt from this, so a host that supplies its own clock is
        // obeyed whichever provider is live. Keeps Infrastructure.AI standalone-safe: composed hosts
        // already register TimeProvider.System through the planner registration and
        // Application.Common, and TryAdd lets whichever ran first win rather than fighting over it.
        services.TryAddSingleton(TimeProvider.System);

        if (appConfig.AI.Conversations.Provider == ConversationStoreProvider.FileSystem)
        {
            services.AddSingleton<IConversationStore, FileSystemConversationStore>();
            return;
        }

        RegisterConversationDbContext(services, appConfig.AI.Conversations);
        services.AddSingleton<IConversationStore, EfCoreConversationStore>();
    }

    /// <summary>
    /// Registers the SQLite-backed <see cref="ConversationDbContext"/> and its schema initializer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered only for the SQLite provider. Nothing outside this store resolves the context, so
    /// the conditional cannot leave another service with an unsatisfiable dependency under
    /// <c>ValidateOnBuild</c>.
    /// </para>
    /// <para>
    /// <see cref="SchemaInitializer{TContext}"/> both creates these tables and reconciles a database
    /// that predates a later column or index. That second step is not idle here: these tables shipped
    /// one release ago, so a consumer already has a <c>conversations.db</c> built from the original
    /// model, and <c>EnsureCreated</c> alone would never give it anything added since.
    /// </para>
    /// <para>
    /// It also leaves the database in write-ahead-log mode, which is what lets one host read a
    /// transcript while another appends to it — under the default rollback journal a writer locks the
    /// whole database and the other host waits out its busy timeout. EF Core's SQLite creator sets
    /// that itself, so nothing here has to; <c>EfCoreConversationStoreTests.Database_IsInWalMode</c>
    /// is the guard that says so out loud, because the multi-host promise depends on it and a
    /// provider change could take it away silently.
    /// </para>
    /// <para>
    /// No <c>SqliteVersionInterceptor</c>: these entities carry no version column deliberately —
    /// see <see cref="Persistence.Entities.ConversationEntity"/> for why a concurrency token would be
    /// a regression here rather than a safeguard.
    /// </para>
    /// </remarks>
    private static void RegisterConversationDbContext(IServiceCollection services, ConversationsConfig config)
    {
        // Checked before combining. A blank value combines to the base directory itself, whose parent
        // is a perfectly good directory name — so the root guard below would pass it through and
        // SQLite would be handed a directory as its DataSource, failing at first use instead of here.
        if (string.IsNullOrWhiteSpace(config.DatabasePath))
        {
            throw new ArgumentException(
                "AppConfig:AI:Conversations:DatabasePath must be set when the Sqlite provider is "
                + "selected.",
                nameof(config));
        }

        var databasePath = Path.Combine(AppContext.BaseDirectory, config.DatabasePath);

        // Deliberately NOT containment-checked the way GovernanceStatePaths checks the
        // governance-state database. That one holds approval verdicts and must stay inside the
        // application directory; this one is meant to be shareable, and two hosts continuing each
        // other's conversations can only do that through a path outside either host's own output
        // directory. Constraining it here would forbid the arrangement the provider exists for.
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "AppConfig:AI:Conversations:DatabasePath must name a file in a directory, not a "
                + "filesystem root.",
                nameof(config));
        }

        Directory.CreateDirectory(directory);

        services.AddDbContextFactory<ConversationDbContext>(options => options
            .UseSqlite($"DataSource={databasePath}"));

        services.AddSingleton<SchemaInitializer<ConversationDbContext>>();
    }
}
