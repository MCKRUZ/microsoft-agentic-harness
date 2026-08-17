using Application.AI.Common.Interfaces.Sandbox;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Coverage for <see cref="SlotReleasingSandboxSession"/>'s disposal contract — specifically the
/// idempotency guard a fresh /code-review pass on #371 found missing: two overlapping disposers of
/// the same session must not double-decrement the host-wide concurrency counter it releases, or the
/// cap silently admits more concurrent sessions than <c>MaxConcurrentSessions</c> permits.
/// </summary>
public sealed class SlotReleasingSandboxSessionTests
{
    [Fact]
    public async Task DisposeAsync_CalledOnce_ReleasesTheSlotExactlyOnce()
    {
        var inner = new FakeSandboxSession();
        var releaseCount = 0;
        var sut = new SlotReleasingSandboxSession(inner, () => releaseCount++);

        await sut.DisposeAsync();

        releaseCount.Should().Be(1);
        inner.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ReleasesTheSlotOnlyOnce()
    {
        // The exact regression this test locks in: without the idempotency guard, a second
        // DisposeAsync call would decrement the concurrency counter a second time, silently
        // widening the effective cap for the rest of the host process's life.
        var inner = new FakeSandboxSession();
        var releaseCount = 0;
        var sut = new SlotReleasingSandboxSession(inner, () => releaseCount++);

        await sut.DisposeAsync();
        await sut.DisposeAsync();

        releaseCount.Should().Be(1);
        inner.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_InnerDisposalThrows_StillReleasesTheSlot()
    {
        var inner = new FakeSandboxSession { ThrowOnDispose = true };
        var releaseCount = 0;
        var sut = new SlotReleasingSandboxSession(inner, () => releaseCount++);

        var act = async () => await sut.DisposeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        releaseCount.Should().Be(1);
    }

    private sealed class FakeSandboxSession : ISandboxSession
    {
        public bool ThrowOnDispose { get; init; }
        public int DisposeCount { get; private set; }

        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput => Stream.Null;
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ThrowOnDispose
                ? ValueTask.FromException(new InvalidOperationException("fake inner disposal failure"))
                : ValueTask.CompletedTask;
        }
    }
}
