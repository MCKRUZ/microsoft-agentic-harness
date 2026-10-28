using System.Reflection;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Compliance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// In the real composition, every store the host resolves that can erase one person is a store the erasure coordinator
/// receives (#900).
/// </summary>
/// <remarks>
/// <para>
/// A store that implements <see cref="IPersonalDataEraser"/> but is never registered as one is the "control nothing
/// invokes" defect: its erasure has unit tests, and erase-my-data still leaves its data behind. This asks the composition
/// itself rather than a list: for every service interface that some eraser type implements, it resolves what the host
/// actually uses and, when that instance can erase, requires the coordinator to hold that very instance.
/// </para>
/// <para>
/// Databases are pointed at a temporary folder so resolving the stores creates nothing in the shared test directory.
/// </para>
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class EveryErasableStoreReachesTheCoordinatorTests : IDisposable
{
    private static readonly Type[] NotStoreContracts = [typeof(IPersonalDataEraser), typeof(IDisposable), typeof(IAsyncDisposable)];

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "composition-erasure-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task EveryResolvedStoreThatCanErase_IsOneTheCoordinatorReceives()
    {
        // Every database a store opens on resolution lives in this test's own folder. The defaults are relative paths
        // shared with every other composition test running in parallel, and two schema initializers racing on one file
        // fail with "table already exists". The governance-state database is left alone: it must sit under the
        // application directory, and durable governance is off by default, so nothing here opens it.
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var coordinator = scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>()
            .Should().BeOfType<PersonalDataErasureCoordinator>().Subject;
        var received = ReceivedErasers(coordinator);

        var contracts = services
            .Where(d => !d.IsKeyedService && d.ServiceType.IsInterface && !d.ServiceType.ContainsGenericParameters)
            .Select(d => d.ServiceType)
            .Where(t => !NotStoreContracts.Contains(t) && EraserTypes.Value.Any(t.IsAssignableFrom))
            .Distinct()
            .ToList();

        var resolvedErasers = contracts
            .Select(contract => (Contract: contract, Instance: scope.ServiceProvider.GetService(contract)))
            .Where(r => r.Instance is IPersonalDataEraser)
            .ToList();

        resolvedErasers.Where(r => !received.Any(e => ReferenceEquals(e, r.Instance)))
            .Select(r => $"{r.Contract.Name} -> {r.Instance!.GetType().Name}")
            .Should().BeEmpty("a store the host uses that can erase must be one erasure reaches; register it with AddPersonalDataEraser");

        // A floor, so this cannot pass on a composition that resolved no erasable stores at all.
        resolvedErasers.Select(r => r.Instance).Distinct().Should().HaveCountGreaterThanOrEqualTo(6,
            "the default composition has conversations, budgets, spills, plans, schedules and the document indexes");
    }

    [Fact]
    public async Task EveryResolvedStoreThatKeepsRuns_IsOneTheCoordinatorAsks()
    {
        // #926, #990: the run stores keep a person's runs in flight through erasure; a store the coordinator does not ask
        // would let the receipt read Full while it still held a record naming them.
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var coordinator = scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>()
            .Should().BeOfType<PersonalDataErasureCoordinator>().Subject;
        var asked = (IReadOnlyList<IKeptRunSource>)typeof(PersonalDataErasureCoordinator)
            .GetField("_keptRuns", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;

        // Every store contract the host resolves whose instance keeps runs, found the way the eraser check above finds
        // erasers, so a store that learns to keep runs later is held to this without being named here.
        var keeping = services
            .Where(d => !d.IsKeyedService && d.ServiceType.IsInterface && !d.ServiceType.ContainsGenericParameters
                && d.ServiceType != typeof(IKeptRunSource) && !NotStoreContracts.Contains(d.ServiceType))
            .Select(d => d.ServiceType)
            .Where(t => KeptRunTypes.Value.Any(t.IsAssignableFrom))
            .Distinct()
            .Select(contract => (Contract: contract, Instance: scope.ServiceProvider.GetService(contract)))
            .Where(r => r.Instance is IKeptRunSource)
            .ToList();

        keeping.Where(r => !asked.Any(a => ReferenceEquals(a, r.Instance)))
            .Select(r => $"{r.Contract.Name} -> {r.Instance!.GetType().Name}")
            .Should().BeEmpty("a store the host uses that keeps runs through erasure must be one the coordinator counts; register it as an IKeptRunSource");
        keeping.Select(r => r.Instance).Distinct().Should().HaveCountGreaterThanOrEqualTo(3,
            "a floor: the workflow run store, the bundle run store and the staged-bundle store")
            .And.Contain(scope.ServiceProvider.GetRequiredService<IRunJobStore>())
            .And.Contain(scope.ServiceProvider.GetRequiredService<IBundleRunJobStore>())
            .And.Contain(scope.ServiceProvider.GetRequiredService<IBundleHandleStore>());
        asked.Should().OnlyHaveUniqueItems("one store is one receipt line");
    }

    private static IReadOnlyList<IPersonalDataEraser> ReceivedErasers(PersonalDataErasureCoordinator coordinator) =>
        (IReadOnlyList<IPersonalDataEraser>)typeof(PersonalDataErasureCoordinator)
            .GetField("_erasers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;

    private static readonly Lazy<IReadOnlyList<Type>> EraserTypes = new(ProductionClassesImplementing<IPersonalDataEraser>);

    private static readonly Lazy<IReadOnlyList<Type>> KeptRunTypes = new(ProductionClassesImplementing<IKeptRunSource>);

    /// <summary>Every concrete class in a loaded, non-dynamic Application or Infrastructure assembly implementing <typeparamref name="T"/>.</summary>
    private static IReadOnlyList<Type> ProductionClassesImplementing<T>() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && ((a.GetName().Name ?? "").StartsWith("Infrastructure.", StringComparison.Ordinal)
            || (a.GetName().Name ?? "").StartsWith("Application.", StringComparison.Ordinal)))
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(t))
        .ToList();
}
