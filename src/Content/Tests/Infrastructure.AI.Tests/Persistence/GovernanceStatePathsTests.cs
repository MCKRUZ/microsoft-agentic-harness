using Infrastructure.AI.Persistence;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

/// <summary>
/// Tests for <see cref="GovernanceStatePaths.EnsureDirectory"/> — path resolution/containment
/// (<see cref="GovernanceStatePaths.Resolve"/>) is covered separately in
/// <c>DurableEscalationHardeningTests</c>.
/// </summary>
public sealed class GovernanceStatePathsTests
{
    // --- Directory permissions (#640, following #527's precedent) ---

    [Fact]
    public void EnsureDirectory_CreatesContainingDirectoryOwnerOnly()
    {
        // #640: this directory holds the approval-verdicts database -- must never inherit whatever
        // the process umask/ACL happens to grant.
        var root = Path.Combine(Path.GetTempPath(), $"gov-perm-check-{Guid.NewGuid():N}");
        var resolved = GovernanceStatePaths.Resolve(".agent-state/governance-state.db", root);

        try
        {
            GovernanceStatePaths.EnsureDirectory(resolved);

            Path.GetDirectoryName(resolved)!.ShouldBeOwnerOnlyDirectory();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
