using System.Reflection;
using Application.AI.Common.Interfaces.Compliance;
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
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
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

    private static IReadOnlyList<IPersonalDataEraser> ReceivedErasers(PersonalDataErasureCoordinator coordinator) =>
        (IReadOnlyList<IPersonalDataEraser>)typeof(PersonalDataErasureCoordinator)
            .GetField("_erasers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;

    private static readonly Lazy<IReadOnlyList<Type>> EraserTypes = new(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && (a.GetName().Name ?? "").StartsWith("Infrastructure.", StringComparison.Ordinal)
            || (a.GetName().Name ?? "").StartsWith("Application.", StringComparison.Ordinal))
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPersonalDataEraser).IsAssignableFrom(t))
        .ToList());
}
