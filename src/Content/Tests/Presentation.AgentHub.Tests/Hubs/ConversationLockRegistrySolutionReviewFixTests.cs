using FluentAssertions;
using Presentation.AgentHub.Hubs;
using Xunit;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Regression coverage for the unbounded per-conversation lock leak fixed in the
/// 2026-06-11 solution review: <see cref="ConversationLockRegistry"/> previously had
/// no way to evict entries, so the singleton dictionary grew without bound. These
/// tests verify the new <see cref="ConversationLockRegistry.Remove"/> eviction path.
/// </summary>
public sealed class ConversationLockRegistrySolutionReviewFixTests
{
    [Fact]
    public void Remove_ExistingFreeLock_EvictsAndDisposes()
    {
        var registry = new ConversationLockRegistry();
        var semaphore = registry.GetOrCreate("conv-1");

        var removed = registry.Remove("conv-1");

        removed.Should().BeTrue();
        registry.Count.Should().Be(0);
        // A disposed semaphore throws when waited on — proves it was disposed.
        var act = () => semaphore.Wait(0);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        var registry = new ConversationLockRegistry();

        var removed = registry.Remove("never-created");

        removed.Should().BeFalse();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void GetOrCreate_AfterRemove_CreatesFreshIndependentLock()
    {
        var registry = new ConversationLockRegistry();
        var first = registry.GetOrCreate("conv-1");

        registry.Remove("conv-1");
        var second = registry.GetOrCreate("conv-1");

        second.Should().NotBeSameAs(first);
        second.CurrentCount.Should().Be(1);
        registry.Count.Should().Be(1);
    }

    [Fact]
    public async Task Remove_HeldLock_EvictsWithoutDisposingInFlightWaiter()
    {
        var registry = new ConversationLockRegistry();
        var semaphore = registry.GetOrCreate("conv-held");
        await semaphore.WaitAsync();

        var removed = registry.Remove("conv-held");

        removed.Should().BeTrue();
        registry.Count.Should().Be(0);
        // Held lock must NOT be disposed — releasing it must still succeed.
        var release = () => semaphore.Release();
        release.Should().NotThrow();
    }

    [Fact]
    public void Registry_AfterManyConversationsRemoved_DoesNotRetainEntries()
    {
        var registry = new ConversationLockRegistry();

        for (var i = 0; i < 1000; i++)
        {
            var id = $"conv-{i}";
            registry.GetOrCreate(id);
            registry.Remove(id);
        }

        // Before the fix there was no Remove path, so Count would equal 1000.
        registry.Count.Should().Be(0);
    }
}
