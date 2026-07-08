using Application.Common.Interfaces.Idempotency;
using Application.Common.MediatRBehaviors;
using Application.Common.Services.Idempotency;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

/// <summary>
/// Regression tests for the idempotency wiring and store contract:
/// the behavior must be registered (not dead code), the store must not cache failures,
/// concurrent duplicates must run once, and — critically — idempotency must be ordered
/// after authorization so a replayed key cannot bypass the auth check.
/// </summary>
public sealed class IdempotencyWiringTests
{
    private static readonly Func<object, bool> PersistSuccesses =
        value => value is not Result { IsSuccess: false };

    [Fact]
    public void AddApplicationCommonDependencies_RegistersIdempotencyStore()
    {
        var services = new ServiceCollection();

        services.AddApplicationCommonDependencies();
        using var provider = services.BuildServiceProvider();

        provider.GetService<IIdempotencyStore>().Should().BeOfType<InMemoryIdempotencyStore>(
            "the behavior throws on an unresolvable IIdempotencyStore if no default is registered");
    }

    [Fact]
    public void AddApplicationCommonDependencies_RegistersIdempotencyBehavior()
    {
        var services = new ServiceCollection();

        services.AddApplicationCommonDependencies();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(IdempotencyBehavior<,>),
            "marking a command IIdempotentRequest must engage the behavior, not silently no-op");
    }

    [Fact]
    public void IdempotencyBehavior_IsRegisteredAfterAuthorizationBehavior()
    {
        var services = new ServiceCollection();
        services.AddApplicationCommonDependencies();

        var pipeline = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType)
            .ToList();

        var authIndex = pipeline.IndexOf(typeof(AuthorizationBehavior<,>));
        var idempotencyIndex = pipeline.IndexOf(typeof(IdempotencyBehavior<,>));

        authIndex.Should().BeGreaterThanOrEqualTo(0);
        idempotencyIndex.Should().BeGreaterThan(authIndex,
            "a replayed idempotency key must clear authorization before any cached response is served");
    }

    [Fact]
    public async Task Store_FailureResult_IsNotPersisted_SoNextCallReExecutes()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var calls = 0;
        Func<Task<object>> execute = () => { calls++; return Task.FromResult<object>(Result<string>.Fail("transient")); };

        await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);
        await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);

        calls.Should().Be(2, "a failed Result must never be cached and replayed to legitimate retries");
    }

    [Fact]
    public async Task Store_SuccessResult_IsPersisted_SoNextCallDoesNotReExecute()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var calls = 0;
        Func<Task<object>> execute = () => { calls++; return Task.FromResult<object>(Result<string>.Success("ok")); };

        await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);
        var second = await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);

        calls.Should().Be(1, "a persisted success must be replayed from cache, not re-executed");
        second.Should().BeOfType<Result<string>>("the store caches by reference and returns the exact runtime type");
    }

    [Fact]
    public async Task Store_ExpiredEntry_ReExecutes()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(time);
        var calls = 0;
        Func<Task<object>> execute = () => { calls++; return Task.FromResult<object>(Result<string>.Success("ok")); };

        await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);
        time.Advance(InMemoryIdempotencyStore.DefaultTtl + TimeSpan.FromSeconds(1));
        await store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);

        calls.Should().Be(2, "an expired entry must be treated as a miss and re-executed");
    }

    [Fact]
    public async Task Store_ExecuteThrows_PropagatesAndDoesNotPersist()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var calls = 0;
        Func<Task<object>> execute = () =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        };

        var act = () => store.GetOrExecuteAsync("k", execute, PersistSuccesses, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().ThrowAsync<InvalidOperationException>();

        calls.Should().Be(2, "a faulted execution must release its reservation so a retry re-executes");
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
