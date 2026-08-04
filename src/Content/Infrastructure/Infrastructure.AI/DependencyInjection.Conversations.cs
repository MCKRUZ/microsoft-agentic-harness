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

        if (appConfig.AI.Conversations.Provider == ConversationStoreProvider.FileSystem)
        {
            services.AddSingleton<IConversationStore, FileSystemConversationStore>();
            return;
        }

        // Keeps Infrastructure.AI standalone-safe. Composed hosts already register
        // TimeProvider.System through the planner registration and Application.Common; TryAdd lets
        // whichever ran first win rather than fighting over it.
        services.TryAddSingleton(TimeProvider.System);

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
    /// The plain <see cref="SchemaInitializer{TContext}"/> is enough here — unlike the planner, whose
    /// subclass exists to add columns to databases created before those columns shipped. These
    /// tables are new in this change, so no consumer has an earlier version of them to evolve, and
    /// <c>EnsureCreated</c> builds them whole.
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
        var databasePath = Path.Combine(AppContext.BaseDirectory, config.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        services.AddDbContextFactory<ConversationDbContext>(options => options
            .UseSqlite($"DataSource={databasePath}"));

        services.AddSingleton<SchemaInitializer<ConversationDbContext>>();
    }
}
