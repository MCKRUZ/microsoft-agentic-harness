using FluentAssertions;
using Infrastructure.AI.Conversations;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// The per-key lock shared by the in-process turn lease and the file-backed conversation store, tested
/// directly: distinct keys do not block each other, the comparer decides what counts as the same key,
/// and entries evict themselves.
/// </summary>
public sealed class KeyedAsyncLockTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var locks = new KeyedAsyncLock(StringComparer.Ordinal);
        using var heldA = await locks.AcquireAsync("a");

        // The whole point of keying the lock: one conversation's I/O must not stall another's.
        using var heldB = await locks.AcquireAsync("b").WaitAsync(TimeSpan.FromSeconds(10));

        locks.TrackedKeys.Should().Be(2);
    }

    [Fact]
    public async Task SameKey_SecondAcquirerWaitsUntilTheFirstReleases()
    {
        var locks = new KeyedAsyncLock(StringComparer.Ordinal);
        var first = await locks.AcquireAsync("a");

        var second = locks.AcquireAsync("a");
        await Task.Delay(Settle);
        second.IsCompleted.Should().BeFalse("the key is held");

        first.Dispose();

        using var _ = await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task KeysDifferingOnlyByCase_ShareALockWhenTheComparerFoldsCase()
    {
        var locks = new KeyedAsyncLock(StringComparer.OrdinalIgnoreCase);
        using var held = await locks.AcquireAsync("Conversation.json");

        var other = locks.AcquireAsync("conversation.JSON");
        await Task.Delay(Settle);

        other.IsCompleted.Should().BeFalse(
            "on a case-folding filesystem these name the same file, so they must name the same lock");
    }

    [Fact]
    public async Task ReleasedKey_LeavesNoEntryBehind()
    {
        var locks = new KeyedAsyncLock(StringComparer.Ordinal);

        using (await locks.AcquireAsync("a"))
            locks.TrackedKeys.Should().Be(1);

        locks.TrackedKeys.Should().Be(0);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotPinTheEntry()
    {
        var locks = new KeyedAsyncLock(StringComparer.Ordinal);
        var held = await locks.AcquireAsync("a");
        using var cts = new CancellationTokenSource();

        var cancelled = locks.AcquireAsync("a", cts.Token);
        await cts.CancelAsync();
        await cancelled.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();
        locks.TrackedKeys.Should().Be(0, "an abandoned wait must hand its reservation back");
    }

    [Fact]
    public async Task DisposingAHolderTwice_ReleasesOnlyOnce()
    {
        var locks = new KeyedAsyncLock(StringComparer.Ordinal);
        var first = await locks.AcquireAsync("a");
        var queuedBehind = locks.AcquireAsync("a");
        var thirdInLine = locks.AcquireAsync("a");

        first.Dispose();
        using var second = await queuedBehind.WaitAsync(TimeSpan.FromSeconds(10));
        first.Dispose();

        await Task.Delay(Settle);
        thirdInLine.IsCompleted.Should().BeFalse(
            "a double dispose must not release the semaphore a second holder now owns");
    }
}
