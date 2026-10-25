using System.Reflection;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services.Compliance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// In the real composition, every store the host resolves that can return one person's data is a store the export
/// receives, and the export's manifest is built from the same stores erasure reaches (#902).
/// </summary>
/// <remarks>
/// <para>
/// A store that implements <see cref="IPersonalDataExporter"/> but is never registered as one is the "control nothing
/// invokes" defect: its export has unit tests, and the person's copy still leaves its data out. This asks the composition
/// itself: for every service interface some exporter type implements, it resolves what the host actually uses and, when
/// that instance can export, requires the export coordinator to hold that very instance.
/// </para>
/// <para>
/// The manifest names a store the copy leaves out only if the coordinator knows of it. It reads the erasure registrations
/// for that, so the second test requires it to receive exactly the stores erasure does.
/// </para>
/// </remarks>
public sealed class EveryExportableStoreReachesTheExportTests : IDisposable
{
    private static readonly Type[] NotStoreContracts =
        [typeof(IPersonalDataExporter), typeof(IPersonalDataEraser), typeof(IDisposable), typeof(IAsyncDisposable)];

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "composition-export-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task EveryResolvedStoreThatCanExport_IsOneTheExportReceives()
    {
        var services = Compose();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var export = ExportCoordinator(scope);
        var received = Field<IPersonalDataExporter>(export, "_exporters");

        var contracts = services
            .Where(d => !d.IsKeyedService && d.ServiceType.IsInterface && !d.ServiceType.ContainsGenericParameters)
            .Select(d => d.ServiceType)
            .Where(t => !NotStoreContracts.Contains(t) && ExporterTypes.Value.Any(t.IsAssignableFrom))
            .Distinct()
            .ToList();

        var resolvedExporters = contracts
            .Select(contract => (Contract: contract, Instance: scope.ServiceProvider.GetService(contract)))
            .Where(r => r.Instance is IPersonalDataExporter)
            .ToList();

        resolvedExporters.Where(r => !received.Any(e => ReferenceEquals(e, r.Instance)))
            .Select(r => $"{r.Contract.Name} -> {r.Instance!.GetType().Name}")
            .Should().BeEmpty("a store the host uses that can export must be one the export reaches; register it with AddPersonalDataExporter");

        // A floor, so this cannot pass on a composition that resolved no exportable stores at all.
        received.Select(e => e.ExportStoreName).Should().Contain(["conversations", "schedules", "knowledge graph"]);
    }

    [Fact]
    public async Task TheExportNamesEveryStoreErasureReaches()
    {
        var services = Compose();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var erasure = scope.ServiceProvider.GetRequiredService<IPersonalDataErasure>()
            .Should().BeOfType<PersonalDataErasureCoordinator>().Subject;
        var export = ExportCoordinator(scope);

        Field<IPersonalDataEraser>(export, "_erasers")
            .Should().BeEquivalentTo(Field<IPersonalDataEraser>(erasure, "_erasers"), o => o.WithStrictOrdering(),
                "the manifest names the stores erasure reaches; it must know of every one");
        Field<RetainedPersonalData>(export, "_retained").Select(r => r.StoreName)
            .Should().Equal(Field<RetainedPersonalData>(erasure, "_retained").Select(r => r.StoreName));
    }

    private ServiceCollection Compose()
    {
        // Every database a store opens on resolution lives in this test's own folder; see the erasure counterpart.
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
            ["AppConfig:AI:Erasure:JournalDatabasePath"] = Path.Combine(_workingDir, "erasure-journal.db"),
        });
        return services;
    }

    private static PersonalDataExportCoordinator ExportCoordinator(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IPersonalDataExport>()
            .Should().BeOfType<PersonalDataExportCoordinator>().Subject;

    private static IReadOnlyList<T> Field<T>(object coordinator, string name) =>
        (IReadOnlyList<T>)coordinator.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(coordinator)!;

    private static readonly Lazy<IReadOnlyList<Type>> ExporterTypes = new(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && (a.GetName().Name ?? "").StartsWith("Infrastructure.", StringComparison.Ordinal)
            || (a.GetName().Name ?? "").StartsWith("Application.", StringComparison.Ordinal))
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPersonalDataExporter).IsAssignableFrom(t))
        .ToList());
}
