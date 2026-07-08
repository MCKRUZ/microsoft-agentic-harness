using Application.Common.Attributes.SecurityAttributes;
using Application.Common.Interfaces.MediatR;
using Application.Common.Interfaces.Security;
using Application.Common.MediatRBehaviors;
using Application.Common.Services.Idempotency;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

/// <summary>
/// Security regression tests for the idempotency access-control hardening:
/// (a) idempotency keys are scoped per user so one user cannot replay another's key,
/// (b) concurrent duplicates run the handler exactly once (no in-flight double execution),
/// (c) idempotency runs after authorization so a replayed key cannot bypass the auth check.
/// </summary>
public sealed class IdempotencySecurityTests
{
    // (a) Cross-user isolation.
    [Fact]
    public async Task ReplayedKeyFromDifferentUser_DoesNotReturnFirstUsersCachedResponse()
    {
        var sharedStore = new InMemoryIdempotencyStore(TimeProvider.System);
        var userA = new IdempotencyBehavior<CrossUserRequest, string>(
            sharedStore, new FakeUser("user-A"), NullLogger<IdempotencyBehavior<CrossUserRequest, string>>.Instance);
        var userB = new IdempotencyBehavior<CrossUserRequest, string>(
            sharedStore, new FakeUser("user-B"), NullLogger<IdempotencyBehavior<CrossUserRequest, string>>.Instance);
        var request = new CrossUserRequest("shared-key");

        var aResult = await userA.Handle(request, () => Task.FromResult("user-A-secret"), CancellationToken.None);
        var bHandlerExecuted = false;
        var bResult = await userB.Handle(
            request,
            () => { bHandlerExecuted = true; return Task.FromResult("user-B-value"); },
            CancellationToken.None);

        aResult.Should().Be("user-A-secret");
        bResult.Should().Be("user-B-value", "user B must never receive user A's cached response");
        bHandlerExecuted.Should().BeTrue("user B's request is a cache miss because keys are scoped per user");
    }

    // (b) Concurrent same-key/same-user requests invoke the handler exactly once.
    [Fact]
    public async Task ConcurrentSameKeySameUser_ExecutesHandlerExactlyOnce()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var sut = new IdempotencyBehavior<CrossUserRequest, Result<string>>(
            store, new FakeUser("user-A"), NullLogger<IdempotencyBehavior<CrossUserRequest, Result<string>>>.Instance);
        var request = new CrossUserRequest("shared-key");
        var calls = 0;

        RequestHandlerDelegate<Result<string>> handler = async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(75);
            return Result<string>.Success("done");
        };

        var a = sut.Handle(request, handler, CancellationToken.None);
        var b = sut.Handle(request, handler, CancellationToken.None);
        var results = await Task.WhenAll(a, b);

        calls.Should().Be(1, "concurrent duplicates must be coalesced into a single handler execution");
        results[0].Should().BeSameAs(results[1], "both concurrent callers observe the one shared response");
    }

    // (c) Idempotency runs after authorization: a replay by a now-unauthorized user is rejected
    // by auth, not served from the cache. Behaviors are composed in registration order
    // (authorization outer, idempotency inner) to mirror the real MediatR pipeline.
    [Fact]
    public async Task UnauthorizedReplay_IsRejectedByAuthorization_NotServedFromCache()
    {
        var store = new InMemoryIdempotencyStore(TimeProvider.System);
        var user = new FakeUser("user-A");
        var identity = new ToggleIdentityService { Allow = true };
        var command = new SecuredCommand("shared-key");

        var authorization = new AuthorizationBehavior<SecuredCommand, Result<string>>(user, identity);
        var idempotency = new IdempotencyBehavior<SecuredCommand, Result<string>>(
            store, user, NullLogger<IdempotencyBehavior<SecuredCommand, Result<string>>>.Instance);

        var handlerCalls = 0;
        RequestHandlerDelegate<Result<string>> handler = () =>
        {
            handlerCalls++;
            return Task.FromResult(Result<string>.Success("secret"));
        };
        RequestHandlerDelegate<Result<string>> idempotencyStep = () => idempotency.Handle(command, handler, CancellationToken.None);

        // First call: authorized -> auth passes -> handler runs -> success cached.
        var first = await authorization.Handle(command, idempotencyStep, CancellationToken.None);

        // Replay after access is revoked: auth must reject before idempotency can serve the cache.
        identity.Allow = false;
        var replay = await authorization.Handle(command, idempotencyStep, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be("secret");
        replay.IsSuccess.Should().BeFalse("authorization runs before idempotency, so a revoked user is denied");
        replay.FailureType.Should().Be(ResultFailureType.Forbidden);
        replay.Value.Should().NotBe("secret", "the cached success must not be leaked to a now-unauthorized replay");
        handlerCalls.Should().Be(1, "the handler ran once for the authorized call and never for the denied replay");
    }

    private sealed record CrossUserRequest(string IdempotencyKey)
        : IRequest<string>, IIdempotentRequest;

    [Authorize(Roles = "admin")]
    private sealed record SecuredCommand(string IdempotencyKey)
        : IRequest<Result<string>>, IIdempotentRequest;

    private sealed class FakeUser(string? id) : IUser
    {
        public string? Id { get; } = id;
        public bool IsAdmin => false;
    }

    private sealed class ToggleIdentityService : IIdentityService
    {
        public bool Allow { get; set; } = true;

        public Task<bool> IsInRoleAsync(string userId, string role) => Task.FromResult(Allow);

        public Task<bool> AuthorizeAsync(string userId, string policyName) => Task.FromResult(Allow);

        public Task<string?> GetUserNameAsync(string userId) => Task.FromResult<string?>(userId);

        public Task<Result<string>> CreateUserAsync(string userName, string password) =>
            throw new NotSupportedException();

        public Task<Result> DeleteUserAsync(string userId) =>
            throw new NotSupportedException();
    }
}
