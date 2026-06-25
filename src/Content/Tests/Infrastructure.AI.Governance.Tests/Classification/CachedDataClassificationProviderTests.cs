using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Classification;

/// <summary>
/// Tests for <see cref="CachedDataClassificationProvider"/>: it memoizes results per asset within the
/// TTL, refreshes after expiry, keys distinct assets separately, can be disabled by a non-positive TTL,
/// and never caches a failure.
/// </summary>
public sealed class CachedDataClassificationProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly AssetReference Asset = new(AssetType.AzureBlob, "https://acct.blob/c/f");
    private static readonly AssetReference OtherAsset = new(AssetType.AzureBlob, "https://acct.blob/c/g");

    [Fact]
    public async Task GetLabelAsync_WithinTtl_ReturnsCachedResultWithoutCallingInner()
    {
        var inner = new CountingProvider();
        var sut = Create(inner, TimeSpan.FromMinutes(5), new MutableTimeProvider(Now));

        await sut.GetLabelAsync(Asset, CancellationToken.None);
        await sut.GetLabelAsync(Asset, CancellationToken.None);

        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetLabelAsync_AfterTtlExpiry_CallsInnerAgain()
    {
        var inner = new CountingProvider();
        var time = new MutableTimeProvider(Now);
        var sut = Create(inner, TimeSpan.FromMinutes(5), time);

        await sut.GetLabelAsync(Asset, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));
        await sut.GetLabelAsync(Asset, CancellationToken.None);

        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetLabelAsync_DistinctAssets_AreCachedSeparately()
    {
        var inner = new CountingProvider();
        var sut = Create(inner, TimeSpan.FromMinutes(5), new MutableTimeProvider(Now));

        await sut.GetLabelAsync(Asset, CancellationToken.None);
        await sut.GetLabelAsync(OtherAsset, CancellationToken.None);
        await sut.GetLabelAsync(Asset, CancellationToken.None);

        inner.CallCount.Should().Be(2, "each distinct asset is resolved once, then served from cache");
    }

    [Fact]
    public async Task GetLabelAsync_NonPositiveTtl_DisablesCaching()
    {
        var inner = new CountingProvider();
        var sut = Create(inner, TimeSpan.Zero, new MutableTimeProvider(Now));

        await sut.GetLabelAsync(Asset, CancellationToken.None);
        await sut.GetLabelAsync(Asset, CancellationToken.None);

        inner.CallCount.Should().Be(2, "a non-positive TTL bypasses the cache entirely");
    }

    [Fact]
    public async Task GetLabelAsync_InnerThrows_DoesNotCacheTheFailure()
    {
        var inner = new CountingProvider { ThrowNext = true };
        var sut = Create(inner, TimeSpan.FromMinutes(5), new MutableTimeProvider(Now));

        var act = async () => await sut.GetLabelAsync(Asset, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // A transient failure must not be frozen into the cache; the next call retries the inner provider.
        var result = await sut.GetLabelAsync(Asset, CancellationToken.None);
        result.Should().NotBeNull();
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetLabelAsync_NullAsset_Throws()
    {
        var sut = Create(new CountingProvider(), TimeSpan.FromMinutes(5), new MutableTimeProvider(Now));

        var act = async () => await sut.GetLabelAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static CachedDataClassificationProvider Create(
        IDataClassificationProvider inner, TimeSpan ttl, TimeProvider time) =>
        new(inner, time, ttl, NullLogger<CachedDataClassificationProvider>.Instance);

    private sealed class CountingProvider : IDataClassificationProvider
    {
        public int CallCount { get; private set; }
        public bool ThrowNext { get; set; }

        public Task<AssetLabelResult> GetLabelAsync(AssetReference asset, CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new InvalidOperationException("backend unreachable");
            }

            return Task.FromResult(AssetLabelResult.Unknown(asset, Now));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
