using System.Globalization;
using Application.AI.Common.Interfaces.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Through the real composition, every audit trail the host verifies also replays what it held (#1001): a record any of
/// the six chains refused reaches it once the host's replay service runs, instead of surviving only as a log line.
/// </summary>
/// <remarks>
/// A trail that is verified but not replayed, a replay service the host never registers, or a writer built without its
/// pending folder all leave a held record where it is, and fail here.
/// </remarks>
[Collection(SqliteConnectionPoolCollection.Name)]
public sealed class EveryAuditTrailReplaysTests : IDisposable
{
    private static readonly string[] Trails = ["governance", "escalations", "changes", "egress", "drift", "compliance"];

    /// <inheritdoc />
    public void Dispose() => SqliteConnection.ClearAllPools();

    [Fact]
    public async Task ReplayService_ARecordHeldByEachTrail_ReachesEveryVerifiedChain()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>());
        var chains = provider.GetServices<IVerifiableAuditChain>().ToList();
        chains.Select(c => c.AuditName).Should().BeEquivalentTo(Trails);
        var held = chains.OfType<IReplaysHeldAuditRecords>().ToList();
        held.Select(t => t.AuditName).Should().BeEquivalentTo(Trails, "a trail that is verified but never replayed loses what it held");

        var before = new Dictionary<string, long>();
        foreach (var chain in chains)
            before[chain.AuditName] = (await chain.VerifyChainAsync(checkpoint: null, CancellationToken.None)).VerifiedCount;
        foreach (var trail in held)
            HoldOne(trail);

        var service = provider.GetServices<IHostedService>().OfType<AuditRecordReplayService>()
            .Should().ContainSingle("every host that registers the trails must replay them").Subject;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await AllWrittenAsync(held);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        foreach (var chain in chains)
        {
            var verified = await chain.VerifyChainAsync(checkpoint: null, CancellationToken.None);
            verified.IsValid.Should().BeTrue(verified.FailureReason);
            verified.VerifiedCount.Should().Be(before[chain.AuditName] + 1, $"the {chain.AuditName} trail must write what it held");
        }
    }

    // As the trail's writer holds a record: one line in a file named by the time it was held.
    private static void HoldOne(IReplaysHeldAuditRecords trail)
    {
        Directory.CreateDirectory(trail.PendingLocation);
        var name = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}.pending");
        File.WriteAllText(
            Path.Combine(trail.PendingLocation, name),
            $"{{\"held_by\":\"{trail.AuditName}\",\"action\":\"probe\",\"timestamp\":\"{DateTimeOffset.UtcNow:O}\"," +
            $"\"recorded_at\":\"{DateTimeOffset.UtcNow:O}\"}}");
    }

    private static async Task AllWrittenAsync(IReadOnlyList<IReplaysHeldAuditRecords> trails)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (trails.All(t => Directory.GetFiles(t.PendingLocation, "*.pending").Length == 0))
                return;
            await Task.Delay(50);
        }
    }
}
