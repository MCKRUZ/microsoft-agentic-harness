using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using Application.Common.Interfaces.Security;
using Application.Common.MediatRBehaviors;
using Application.Common.Services.Idempotency;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public sealed class IdempotencyBehaviorTests
{
    private const string UserId = "user-1";

    [Fact]
    public async Task Handle_NonIdempotentRequest_PassesThrough()
    {
        var store = new Mock<IIdempotencyStore>();
        var sut = CreateBehavior(store.Object, UserId);
        var called = false;

        await sut.Handle(
            new RegularRequest(),
            () => { called = true; return Task.FromResult("result"); },
            CancellationToken.None);

        called.Should().BeTrue();
        store.Verify(
            s => s.GetOrExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<Func<object, bool>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "non-idempotent requests must not touch the idempotency store");
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheMiss_ExecutesHandlerAndReturnsFreshResult()
    {
        var sut = CreateBehavior(new InMemoryIdempotencyStore(TimeProvider.System), UserId);
        var called = false;

        var result = await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => { called = true; return Task.FromResult("fresh-result"); },
            CancellationToken.None);

        called.Should().BeTrue();
        result.Should().Be("fresh-result");
    }

    [Fact]
    public async Task Handle_IdempotentRequest_SecondCall_ReturnsCachedAndDoesNotReExecute()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var sut = CreateBehavior(store, UserId);
        var calls = 0;
        var request = new IdempotentTestRequest("key-1");

        await sut.Handle(request, () => { calls++; return Task.FromResult("first"); }, CancellationToken.None);
        var second = await sut.Handle(request, () => { calls++; return Task.FromResult("second"); }, CancellationToken.None);

        calls.Should().Be(1, "the second call with the same key must be served from cache");
        second.Should().Be("first");
    }

    [Fact]
    public async Task Handle_DifferentKeys_ExecuteHandlerForEach()
    {
        var sut = CreateBehavior(new InMemoryIdempotencyStore(TimeProvider.System), UserId);
        var callCount = 0;

        await sut.Handle(new IdempotentTestRequest("key-a"), () => { callCount++; return Task.FromResult("a"); }, CancellationToken.None);
        await sut.Handle(new IdempotentTestRequest("key-b"), () => { callCount++; return Task.FromResult("b"); }, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_ComposesStoreKeyFromUserRequestTypeAndIdempotencyKey()
    {
        var store = new Mock<IIdempotencyStore>();
        string? capturedKey = null;
        store
            .Setup(s => s.GetOrExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<Func<object, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Func<Task<object>>, Func<object, bool>, CancellationToken>((k, _, _, _) => capturedKey = k)
            .ReturnsAsync("value");

        var sut = new IdempotencyBehavior<IdempotentTestRequest, string>(
            store.Object, new FakeUser(UserId), NullLogger<IdempotencyBehavior<IdempotentTestRequest, string>>.Instance);

        await sut.Handle(new IdempotentTestRequest("raw-key"), () => Task.FromResult("value"), CancellationToken.None);

        capturedKey.Should().Be($"{UserId}:{typeof(IdempotentTestRequest).FullName}:raw-key",
            "the store key must be scoped by user id and request type so keys cannot collide across users or commands");
    }

    [Fact]
    public async Task Handle_UnauthenticatedUser_UsesAnonymousKeySegment()
    {
        var store = new Mock<IIdempotencyStore>();
        string? capturedKey = null;
        store
            .Setup(s => s.GetOrExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<Func<object, bool>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Func<Task<object>>, Func<object, bool>, CancellationToken>((k, _, _, _) => capturedKey = k)
            .ReturnsAsync("value");

        var sut = new IdempotencyBehavior<IdempotentTestRequest, string>(
            store.Object, new FakeUser(id: null), NullLogger<IdempotencyBehavior<IdempotentTestRequest, string>>.Instance);

        await sut.Handle(new IdempotentTestRequest("raw-key"), () => Task.FromResult("value"), CancellationToken.None);

        capturedKey.Should().StartWith("anonymous:", "a null identity must resolve to a stable anonymous segment, not an empty prefix");
    }

    [Fact]
    public async Task Handle_FailureResult_IsNotCached_SoRetryReExecutes()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var sut = new IdempotencyBehavior<IdempotentTestRequest, Result<string>>(
            store, new FakeUser(UserId), NullLogger<IdempotencyBehavior<IdempotentTestRequest, Result<string>>>.Instance);
        var request = new IdempotentTestRequest("key-fail");
        var calls = 0;

        await sut.Handle(request, () => { calls++; return Task.FromResult(Result<string>.Fail("transient")); }, CancellationToken.None);
        await sut.Handle(request, () => { calls++; return Task.FromResult(Result<string>.Fail("transient")); }, CancellationToken.None);

        calls.Should().Be(2, "a failed Result must never be cached, so a legitimate retry re-executes the handler");
    }

    private static IdempotencyBehavior<object, string> CreateBehavior(IIdempotencyStore store, string? userId) =>
        new(store, new FakeUser(userId), NullLogger<IdempotencyBehavior<object, string>>.Instance);

    private sealed record RegularRequest;

    private sealed record IdempotentTestRequest(string IdempotencyKey) : IIdempotentRequest;

    private sealed class FakeUser(string? id) : IUser
    {
        public string? Id { get; } = id;
        public bool IsAdmin => false;
    }
}
