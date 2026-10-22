using Application.AI.Common.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace Application.AI.Common.Tests.Services;

/// <summary>
/// Nothing written through <see cref="ExpiringDistributedCache"/> outlives its maximum lifetime (#901), whatever the
/// caller asked for, and a shorter expiry the caller asked for is kept.
/// </summary>
public sealed class ExpiringDistributedCacheTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IDistributedCache> _inner = new();
    private DistributedCacheEntryOptions? _written;

    public ExpiringDistributedCacheTests()
    {
        _inner.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, _, o, _) => _written = o)
            .Returns(Task.CompletedTask);
        _inner.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>()))
            .Callback<string, byte[], DistributedCacheEntryOptions>((_, _, o) => _written = o);
    }

    private ExpiringDistributedCache Cache => new(_inner.Object, Hour, _time);

    [Fact]
    public async Task AnEntryWithNoExpiry_IsGivenTheMaximum()
    {
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions());

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public void TheSynchronousWrite_IsBoundedToo()
    {
        Cache.Set("k", [1], new DistributedCacheEntryOptions());

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public async Task ALaterAbsoluteExpiry_IsCapped()
    {
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions { AbsoluteExpiration = _time.GetUtcNow().AddDays(30) });

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public async Task ALongerRelativeExpiry_IsCapped()
    {
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(2) });

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public async Task AnEarlierExpiry_IsKept()
    {
        var soon = _time.GetUtcNow().AddMinutes(5);
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions { AbsoluteExpiration = soon });

        _written!.AbsoluteExpiration.Should().Be(soon);
    }

    [Fact]
    public async Task ASlidingWindow_NeverOutlivesTheMaximum()
    {
        // A sliding window renewed by every read would keep a popular entry forever without the absolute cap.
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(1) });

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public async Task AHugeRequestedLifetime_IsCappedWithoutOverflowing()
    {
        await Cache.SetAsync("k", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.MaxValue });

        _written!.AbsoluteExpiration.Should().Be(_time.GetUtcNow() + Hour);
    }

    [Fact]
    public void ANonPositiveLifetime_IsRefused()
    {
        var act = () => new ExpiringDistributedCache(_inner.Object, TimeSpan.Zero, _time);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
