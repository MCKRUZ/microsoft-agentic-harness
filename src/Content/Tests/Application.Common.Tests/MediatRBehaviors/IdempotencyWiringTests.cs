using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using Application.Common.MediatRBehaviors;
using Application.Common.Services.Idempotency;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

/// <summary>
/// Regression tests for the idempotency wiring defect (solution-review finding 25):
/// IdempotencyBehavior was dead code — never registered and IIdempotencyStore had no
/// implementation — and the behavior cached failure results unconditionally.
/// </summary>
public sealed class IdempotencyWiringTests
{
    [Fact]
    public void AddApplicationCommonDependencies_RegistersIdempotencyStore()
    {
        var services = new ServiceCollection();

        services.AddApplicationCommonDependencies();
        using var provider = services.BuildServiceProvider();

        var store = provider.GetService<IIdempotencyStore>();
        store.Should().BeOfType<InMemoryIdempotencyStore>(
            "the behavior throws on an unresolvable IIdempotencyStore if no default is registered");
    }

    [Fact]
    public void AddApplicationCommonDependencies_RegistersIdempotencyBehavior()
    {
        var services = new ServiceCollection();

        services.AddApplicationCommonDependencies();

        // Inspect the registration rather than resolving the whole pipeline (which would also
        // construct the pre-existing AuthorizationBehavior and its auth-stack dependencies). The
        // behavior is registered as an open-generic pipeline behavior.
        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(IdempotencyBehavior<,>),
            "marking a command IIdempotentRequest must engage the behavior, not silently no-op");
    }

    [Fact]
    public async Task Handle_FailureResult_IsNotCached()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var sut = new IdempotencyBehavior<IdempotentCommand, Result<string>>(
            store, NullLogger<IdempotencyBehavior<IdempotentCommand, Result<string>>>.Instance);
        var request = new IdempotentCommand("key-fail");

        var first = await sut.Handle(request, () => Task.FromResult(Result<string>.Fail("transient")), CancellationToken.None);
        var cached = await store.TryGetAsync("key-fail", CancellationToken.None);

        first.IsSuccess.Should().BeFalse();
        cached.Should().BeNull("a failed Result must never be cached and replayed to legitimate retries");
    }

    [Fact]
    public async Task Handle_FailureThenSuccess_SecondCallReExecutesAndCachesSuccess()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var sut = new IdempotencyBehavior<IdempotentCommand, Result<string>>(
            store, NullLogger<IdempotencyBehavior<IdempotentCommand, Result<string>>>.Instance);
        var request = new IdempotentCommand("key-retry");
        var calls = 0;

        RequestHandlerDelegate<Result<string>> handler = () =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Result<string>.Fail("transient")
                : Result<string>.Success("ok"));
        };

        await sut.Handle(request, handler, CancellationToken.None);
        var second = await sut.Handle(request, handler, CancellationToken.None);

        calls.Should().Be(2, "the failure must not be cached, so the retry re-executes the handler");
        second.IsSuccess.Should().BeTrue();
        (await store.TryGetAsync("key-retry", CancellationToken.None)).Should().NotBeNull(
            "the eventual success must be cached for subsequent retries");
    }

    [Fact]
    public async Task Store_SuccessResult_RoundTripsExactRuntimeType()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var response = Result<string>.Success("value");

        await store.SetAsync("k", response, CancellationToken.None);
        var cached = await store.TryGetAsync("k", CancellationToken.None);

        cached.Should().BeOfType<Result<string>>(
            "the in-memory store caches by reference so the behavior's 'cached is TResponse' check holds");
    }

    [Fact]
    public async Task Store_ExpiredEntry_ReturnsNull()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(time);

        await store.SetAsync("k", "v", CancellationToken.None);
        time.Advance(InMemoryIdempotencyStore.DefaultTtl + TimeSpan.FromSeconds(1));
        var cached = await store.TryGetAsync("k", CancellationToken.None);

        cached.Should().BeNull("entries must expire after their TTL");
    }

    private sealed record IdempotentCommand(string IdempotencyKey)
        : IRequest<Result<string>>, IIdempotentRequest;

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
