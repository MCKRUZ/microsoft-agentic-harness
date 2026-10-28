using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Seeds the bundle API's two in-memory stores the way a bundle run writes them (#990), for the erasure and export
/// end-to-end tests: a staged bundle under the person's id, and a finished run of it carrying their message.
/// </summary>
internal static class BundleStoreSeeds
{
    /// <summary>The job id the seeded run is stored under.</summary>
    public static string RunId(string label) => $"bundle-run-{label}";

    /// <summary>The handle the seeded bundle is stored under.</summary>
    public static string HandleOf(string label) => $"bundle-{label}";

    /// <summary>Admits a run of <paramref name="label"/>'s bundle and finishes it, so erasure may remove it.</summary>
    public static void SeedFinishedRun(IServiceProvider sp, string ownerId, string label)
    {
        var runs = sp.GetRequiredService<IBundleRunJobStore>();
        var run = new BundleRunRecord
        {
            JobId = RunId(label),
            Handle = HandleOf(label),
            OwnerId = ownerId,
            AgentName = $"agent-{label}",
            UserMessages = [$"{label} bundle message"],
            MaxTurns = 1,
            Envelope = new CapabilityEnvelope(),
            Status = BundleRunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        runs.TryCreate(run, int.MaxValue).Should().Be(BundleRunAdmission.Accepted);
        runs.Update(run with { Status = BundleRunStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow }).Should().BeTrue();
    }

    /// <summary>Stages <paramref name="label"/>'s bundle in a folder of its own under <paramref name="root"/>.</summary>
    public static void SeedStagedBundle(IServiceProvider sp, string root, string ownerId, string label)
    {
        var folder = Path.Combine(root, "staged", label);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AGENT.md"), "# agent");
        sp.GetRequiredService<IBundleHandleStore>().Register(new StagedBundle
        {
            BundleId = HandleOf(label),
            StagedRootDirectory = folder,
            Agent = new AgentDefinition { Id = $"agent-{label}", Name = $"{label} agent" },
        }, ownerId);
    }
}
