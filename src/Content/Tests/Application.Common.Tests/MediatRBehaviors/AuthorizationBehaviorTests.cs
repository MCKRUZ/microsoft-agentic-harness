using Application.Common.Attributes.SecurityAttributes;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Common.Interfaces.Security;
using Application.Common.MediatRBehaviors;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Moq;
using Xunit;

namespace Application.Common.Tests.MediatRBehaviors;

public class AuthorizationBehaviorTests
{
    private readonly Mock<IUser> _user = new();
    private readonly Mock<IIdentityService> _identityService = new();

    // --- Request types for testing (attributes are read via reflection) ---

    private record NoAuthRequest : IRequest<Result<string>>;

    [Authorize(Roles = "Admin")]
    private record SingleRoleRequest : IRequest<Result<string>>;

    [Authorize(Roles = "Admin,Manager")]
    private record MultiRoleRequest : IRequest<Result<string>>;

    [Authorize(Policy = "CanEdit")]
    private record PolicyRequest : IRequest<Result<string>>;

    [Authorize(Roles = "Admin")]
    [Authorize(Policy = "CanEdit")]
    private record RoleAndPolicyRequest : IRequest<Result<string>>;

    [Authorize(Roles = "Admin")]
    private record NonResultRequest : IRequest<string>;

    private AuthorizationBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new AuthorizationBehavior<TRequest, TResponse>(
            _user.Object, _identityService.Object);
    }

    private static RequestHandlerDelegate<T> NextReturning<T>(T value) =>
        () => Task.FromResult(value);

    [Fact]
    public async Task Handle_NoAuthorizeAttributes_PassesThrough()
    {
        var behavior = CreateBehavior<NoAuthRequest, Result<string>>();
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new NoAuthRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_NoUserId_ReturnsUnauthorizedResult()
    {
        _user.Setup(u => u.Id).Returns((string?)null);
        var behavior = CreateBehavior<SingleRoleRequest, Result<string>>();

        var result = await behavior.Handle(
            new SingleRoleRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Unauthorized);
    }

    [Fact]
    public async Task Handle_NoUserId_NonResultResponse_ThrowsUnauthorizedAccessException()
    {
        _user.Setup(u => u.Id).Returns((string?)null);
        var behavior = CreateBehavior<NonResultRequest, string>();

        var act = () => behavior.Handle(
            new NonResultRequest(),
            NextReturning("should not reach"),
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Authentication is required*");
    }

    [Fact]
    public async Task Handle_SingleRoleAuthorized_PassesThrough()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.IsInRoleAsync("user-1", "Admin")).ReturnsAsync(true);
        var behavior = CreateBehavior<SingleRoleRequest, Result<string>>();
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new SingleRoleRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_SingleRoleUnauthorized_ReturnsForbiddenResult()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.IsInRoleAsync("user-1", "Admin")).ReturnsAsync(false);
        var behavior = CreateBehavior<SingleRoleRequest, Result<string>>();

        var result = await behavior.Handle(
            new SingleRoleRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain(e => e.Contains("Admin"));
    }

    [Fact]
    public async Task Handle_MultipleRoles_AnyMatch_PassesThrough()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.IsInRoleAsync("user-1", "Admin")).ReturnsAsync(false);
        _identityService.Setup(s => s.IsInRoleAsync("user-1", "Manager")).ReturnsAsync(true);
        var behavior = CreateBehavior<MultiRoleRequest, Result<string>>();
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new MultiRoleRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_MultipleRoles_NoneMatch_ReturnsForbidden()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.IsInRoleAsync("user-1", It.IsAny<string>())).ReturnsAsync(false);
        var behavior = CreateBehavior<MultiRoleRequest, Result<string>>();

        var result = await behavior.Handle(
            new MultiRoleRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
    }

    [Fact]
    public async Task Handle_PolicyAuthorized_PassesThrough()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.AuthorizeAsync("user-1", "CanEdit")).ReturnsAsync(true);
        var behavior = CreateBehavior<PolicyRequest, Result<string>>();
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new PolicyRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_PolicyUnauthorized_ReturnsForbidden()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.AuthorizeAsync("user-1", "CanEdit")).ReturnsAsync(false);
        var behavior = CreateBehavior<PolicyRequest, Result<string>>();

        var result = await behavior.Handle(
            new PolicyRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().Contain(e => e.Contains("CanEdit"));
    }

    [Fact]
    public async Task Handle_RoleUnauthorized_NonResultResponse_ThrowsForbiddenAccessException()
    {
        _user.Setup(u => u.Id).Returns("user-1");
        _identityService.Setup(s => s.IsInRoleAsync("user-1", "Admin")).ReturnsAsync(false);
        var behavior = CreateBehavior<NonResultRequest, string>();

        var act = () => behavior.Handle(
            new NonResultRequest(),
            NextReturning("should not reach"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }
}
