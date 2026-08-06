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
/// <strong>Ordering does not matter and must not.</strong> This asserts on files, and any test that
/// creates one has already created it by the time this runs — or will, and then this fails on the next
/// run. It is deliberately not a "run me last" test, because a test that only works in one position is
/// a test that stops working.
/// </para>
/// </remarks>
public sealed class BuildOutputStaysCleanTests
{
    [Fact]
    public void NoDatabaseIsWrittenIntoTheBuildOutput()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

        // Absent is the normal outcome; present-but-empty is also fine, because registration creates the
        // directory eagerly for a path it may never write to. Only actual state is the defect.
        var leftBehind = Directory.Exists(dataDirectory)
            ? Directory.GetFiles(dataDirectory, "*", SearchOption.AllDirectories)
            : [];

        leftBehind.Should().BeEmpty(
            $"state under AppContext.BaseDirectory is never cleared between runs, so it accumulates "
            + $"invisibly on a developer machine and shows up as a product regression CI cannot "
            + $"reproduce. Either a test registered a store without building its config through "
            + $"IsolatedAppConfig, or this machine still holds state from a run predating that helper "
            + $"— delete '{dataDirectory}' and run again to tell the two apart");
    }
}
