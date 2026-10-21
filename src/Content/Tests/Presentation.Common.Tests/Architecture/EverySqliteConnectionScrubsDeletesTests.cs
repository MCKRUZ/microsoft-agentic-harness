using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Tests.Composition;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Every SQLite connection the harness opens overwrites deleted rows (#900), so an erased person's text is not left in the
/// database file's free pages.
/// </summary>
/// <remarks>
/// <para>
/// Secure delete is a per-connection setting, so one connection opened the ordinary way is enough to leave erased text
/// readable. Two guards hold it. The compiled-code scan requires every production call that opens SQLite, a
/// <c>SqliteConnection</c> or Entity Framework's <c>UseSqlite</c>, to sit in the two helpers that turn it on. The
/// composition check opens a connection from every SQLite context the host registers and reads the setting back.
/// </para>
/// <para>
/// <c>ErasureReachesEveryStoreEndToEndTests.ErasedText_IsNotLeftInTheDatabaseFilesOnDisk</c> is the behavioural proof:
/// it scans the bytes of the database files after an erasure.
/// </para>
/// </remarks>
public sealed class EverySqliteConnectionScrubsDeletesTests : IDisposable
{
    private const string UseSqlite = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions::UseSqlite";
    private const string NewConnection = "Microsoft.Data.Sqlite.SqliteConnection::.ctor";

    /// <summary>The only production types allowed to open SQLite, because each turns secure delete on.</summary>
    private static readonly string[] Sanctioned =
    [
        PersistenceScan.KeyOf("Infrastructure.AI.RAG", "Infrastructure.AI.RAG.Sqlite.SqliteSecureDelete"),
        PersistenceScan.KeyOf("Infrastructure.AI", "Infrastructure.AI.Extensions.SqliteDbContextOptionsBuilderExtensions"),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, PersistenceScan.TypeFacts>> Facts = new(() =>
        PersistenceScan.Scan(PersistenceScan.ProductionAssemblyPaths(Path.Combine(RepoRoot.Path, "src", "Content"))));

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "sqlite-secure-delete-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public void OnlyTheSecureDeleteHelpers_OpenSqlite()
    {
        var openers = Facts.Value
            .Where(f => f.Value.ExternalCalls.Contains(UseSqlite) || f.Value.ExternalCalls.Contains(NewConnection))
            .Select(f => f.Key)
            .ToList();

        string.Join(Environment.NewLine, openers.Except(Sanctioned)).Should().BeEmpty(
            "a SQLite connection opened outside SqliteSecureDelete or UseSqliteWithSecureDelete leaves deleted rows readable");
        openers.Should().Contain(Sanctioned, "the scan must see the helpers' own calls, or it is not seeing calls at all");
    }

    [Fact]
    public async Task EverySqliteContextTheHostRegisters_HasSecureDeleteOn()
    {
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:PersistenceEnabled"] = "true",
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:SkillTraining:CheckpointDatabasePath"] = Path.Combine(_workingDir, "skill-training.db"),
            ["AppConfig:AI:EvalDashboard:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "eval.db")}",
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var factories = services
            .Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();

        var checkedContexts = new List<string>();
        var missing = new List<string>();
        foreach (var factoryType in factories)
        {
            var factory = provider.GetRequiredService(factoryType);
            await using var context = (DbContext)factoryType.GetMethod(nameof(IDbContextFactory<DbContext>.CreateDbContext))!
                .Invoke(factory, null)!;
            if (!context.Database.IsSqlite())
                continue;

            var name = context.GetType().Name;
            if (name == "GovernanceStateDbContext")
                continue; // Its path must sit under the application directory; opening it here would write there.

            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA secure_delete;";
            var value = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            checkedContexts.Add(name);
            if (value != 1)
                missing.Add(name);
        }

        string.Join(", ", missing).Should().BeEmpty("every SQLite context must turn secure delete on when it connects");
        checkedContexts.Should().Contain(["ConversationDbContext", "PlannerDbContext", "ScheduleDbContext", "PromptUsageDbContext"],
            "a floor, so this cannot pass by checking nothing");
    }
}
