using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests;

/// <summary>
/// This assembly must not persist anything into its own build output.
/// </summary>
/// <remarks>
/// <para>
/// Issue #262, and #250 and #259 before it. State written under <c>AppContext.BaseDirectory</c> is never
/// cleared between runs, so it accumulates across every run a developer has ever done on that machine.
/// CI never sees it, because CI starts from a clean checkout — which is exactly why it survives, and why
/// it eventually presents as a product regression that reproduces on one machine and nowhere else.
/// </para>
/// <para>
/// <strong>Ordering does matter, and the runner is what guarantees it.</strong> This sits in a
/// <c>DisableParallelization</c> collection, and xUnit runs those only once every parallel collection
/// has finished — so the tests that register stores have all run by the time this reads the directory.
/// An earlier draft left it unattributed on the reasoning that a leak would simply fail the next run.
/// That reasoning holds on a developer machine and fails exactly where it matters: CI starts from a
/// clean checkout every time, so "the next run" never sees it, and the guard was a coin flip against
/// the very tests it exists to police.
/// </para>
/// </remarks>
[Collection(SerialTailCollection.Name)]
public sealed class BuildOutputStaysCleanTests
{
    [Fact]
    public void NoDatabaseIsWrittenIntoTheBuildOutput()
    {
        var baseDirectory = AppContext.BaseDirectory;

        // Every database anywhere under the build output, not just the `data` folder the two known
        // offenders used. A config path that resolves to empty lands its file at the build-output root
        // instead, which a folder-scoped check would never see — and an empty path is precisely what a
        // regression in the isolation helper would produce.
        var leftBehind = Directory.GetFiles(baseDirectory, "*.db", SearchOption.AllDirectories);

        leftBehind.Should().BeEmpty(
            "state under AppContext.BaseDirectory is never cleared between runs, so it accumulates "
            + "invisibly on a developer machine and shows up as a product regression CI cannot "
            + "reproduce. Either a test registered a store without building its config through "
            + $"IsolatedAppConfig, or this machine still holds state from a run predating that helper — "
            + $"delete the databases under '{baseDirectory}' and run again to tell the two apart");
    }
}
