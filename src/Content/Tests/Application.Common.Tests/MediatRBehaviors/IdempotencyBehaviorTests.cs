using Application.Common.Interfaces.Idempotency;
using Application.Common.Interfaces.MediatR;
using Application.Common.MediatRBehaviors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public sealed class IdempotencyBehaviorTests
{
    private readonly Mock<IIdempotencyStore> _store = new();

    [Fact]
    public async Task Handle_NonIdempotentRequest_PassesThrough()
    {
        var sut = CreateBehavior();
        var called = false;

        await sut.Handle(
            new RegularRequest(),
            () => { called = true; return Task.FromResult("result"); },
            CancellationToken.None);

        called.Should().BeTrue();
        _store.Verify(s => s.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NonIdempotentRequest_NeverWritesToStore()
    {
        var sut = CreateBehavior();

        await sut.Handle(
            new RegularRequest(),
            () => Task.FromResult("result"),
            CancellationToken.None);

        _store.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheMiss_ExecutesHandler()
    {
        _store.Setup(s => s.TryGetAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        var sut = CreateBehavior();
        var called = false;

        var result = await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => { called = true; return Task.FromResult("fresh-result"); },
            CancellationToken.None);

        called.Should().BeTrue();
        result.Should().Be("fresh-result");
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheMiss_StoresResponse()
    {
        _store.Setup(s => s.TryGetAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        var sut = CreateBehavior();

        await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => Task.FromResult("fresh-result"),
            CancellationToken.None);

        _store.Verify(s => s.SetAsync("key-1", "fresh-result", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheHit_ReturnsCachedResponse()
    {
        _store.Setup(s => s.TryGetAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("cached-result");

        var sut = CreateBehavior();

        var result = await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => Task.FromResult("fresh-result"),
            CancellationToken.None);

        result.Should().Be("cached-result");
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheHit_DoesNotExecuteHandler()
    {
        _store.Setup(s => s.TryGetAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("cached-result");

        var sut = CreateBehavior();
        var called = false;

        await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => { called = true; return Task.FromResult("fresh-result"); },
            CancellationToken.None);

        called.Should().BeFalse("handler must not execute when the cache has a hit");
    }

    [Fact]
    public async Task Handle_IdempotentRequest_CacheHit_NeverWritesToStore()
    {
        _store.Setup(s => s.TryGetAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("cached-result");

        var sut = CreateBehavior();

        await sut.Handle(
            new IdempotentTestRequest("key-1"),
            () => Task.FromResult("fresh-result"),
            CancellationToken.None);

        _store.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DifferentKeys_ExecuteHandlerForEach()
    {
        _store.Setup(s => s.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);

        var sut = CreateBehavior();
        var callCount = 0;

        await sut.Handle(new IdempotentTestRequest("key-a"), () => { callCount++; return Task.FromResult("a"); }, CancellationToken.None);
        await sut.Handle(new IdempotentTestRequest("key-b"), () => { callCount++; return Task.FromResult("b"); }, CancellationToken.None);

        callCount.Should().Be(2);
        _store.Verify(s => s.SetAsync("key-a", "a", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SetAsync("key-b", "b", It.IsAny<CancellationToken>()), Times.Once);
    }

    private IdempotencyBehavior<object, string> CreateBehavior() =>
        new(_store.Object, NullLogger<IdempotencyBehavior<object, string>>.Instance);

    private sealed record RegularRequest;

    private sealed record IdempotentTestRequest(string IdempotencyKey) : IIdempotentRequest;
}
