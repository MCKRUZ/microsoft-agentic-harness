using Application.Common.Interfaces.MediatR;
using Application.Common.MediatRBehaviors;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public class CachingBehaviorTests : IDisposable
{
    private readonly HybridCache _cache;
    private readonly MemoryCache _memoryCache;
    private readonly ServiceProvider _serviceProvider;

    public CachingBehaviorTests()
    {
        _memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryCache>(_memoryCache);
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();
        _cache = _serviceProvider.GetRequiredService<HybridCache>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _memoryCache.Dispose();
        GC.SuppressFinalize(this);
    }

    private record TestQuery(string CacheKey) : ICacheableQuery<string>
    {
        public HybridCacheEntryOptions? CacheOptions => null;
    }

    private record TestCommand : IRequest<string>, ICacheInvalidation
    {
        public IReadOnlyList<string> CacheKeysToInvalidate { get; init; } = [];
    }

    private record PlainRequest : IRequest<string>;

    private static RequestHandlerDelegate<T> NextReturning<T>(T value) =>
        () => Task.FromResult(value);

    [Fact]
    public async Task Handle_NonCacheableRequest_PassesThrough()
    {
        var behavior = new CachingBehavior<PlainRequest, string>(
            _cache,
            NullLogger<CachingBehavior<PlainRequest, string>>.Instance);

        var result = await behavior.Handle(
            new PlainRequest(), NextReturning("direct"), CancellationToken.None);

        result.Should().Be("direct");
    }

    [Fact]
    public async Task Handle_CacheableQuery_CacheMiss_CallsHandlerAndStoresResult()
    {
        var behavior = new CachingBehavior<TestQuery, string>(
            _cache,
            NullLogger<CachingBehavior<TestQuery, string>>.Instance);

        var callCount = 0;
        RequestHandlerDelegate<string> next = () =>
        {
            callCount++;
            return Task.FromResult("computed-value");
        };

        var result1 = await behavior.Handle(
            new TestQuery("key-1"), next, CancellationToken.None);
        var result2 = await behavior.Handle(
            new TestQuery("key-1"), next, CancellationToken.None);

        result1.Should().Be("computed-value");
        result2.Should().Be("computed-value");
        callCount.Should().Be(1, "second call should use cached value");
    }

    [Fact]
    public async Task Handle_CacheInvalidation_RemovesCacheKeys()
    {
        var queryBehavior = new CachingBehavior<TestQuery, string>(
            _cache,
            NullLogger<CachingBehavior<TestQuery, string>>.Instance);

        // Populate cache
        await queryBehavior.Handle(
            new TestQuery("key-to-invalidate"),
            NextReturning("cached-value"),
            CancellationToken.None);

        // Invalidate
        var commandBehavior = new CachingBehavior<TestCommand, string>(
            _cache,
            NullLogger<CachingBehavior<TestCommand, string>>.Instance);

        await commandBehavior.Handle(
            new TestCommand { CacheKeysToInvalidate = ["key-to-invalidate"] },
            NextReturning("command-result"),
            CancellationToken.None);

        // Verify cache was invalidated by checking handler is called again
        var callCount = 0;
        RequestHandlerDelegate<string> next = () =>
        {
            callCount++;
            return Task.FromResult("recomputed");
        };

        var result = await queryBehavior.Handle(
            new TestQuery("key-to-invalidate"), next, CancellationToken.None);

        result.Should().Be("recomputed");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_CacheInvalidation_ExecutesHandlerFirst()
    {
        var behavior = new CachingBehavior<TestCommand, string>(
            _cache,
            NullLogger<CachingBehavior<TestCommand, string>>.Instance);

        var result = await behavior.Handle(
            new TestCommand { CacheKeysToInvalidate = ["some-key"] },
            NextReturning("handler-result"),
            CancellationToken.None);

        result.Should().Be("handler-result");
    }
}
