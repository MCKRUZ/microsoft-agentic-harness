using System.Security.Claims;
using Domain.Common.Constants;
using Domain.Common.Enums;
using FluentAssertions;
using Infrastructure.APIAccess.Auth.Handlers;
using Infrastructure.APIAccess.Auth.Requirements;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Auth;

public class PermissionAuthHandlerTests
{
    private readonly PermissionAuthHandler _handler = new();

    [Fact]
    public async Task HandleRequirementAsync_AccessPermission_AlwaysSucceeds()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Access);
        var user = CreateAuthenticatedUser();
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminWithAdminClaim_Succeeds()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Admin);
        var user = CreateAuthenticatedUser(new Claim(ClaimConstants.IsAdmin, "true"));
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirementAsync_AdminWithoutAdminClaim_Fails()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Admin);
        var user = CreateAuthenticatedUser();
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirementAsync_TermsAgreementWithClaim_Succeeds()
    {
        var requirement = new PermissionRequirement(AuthPermissions.TermsAgreement);
        var user = CreateAuthenticatedUser(new Claim(ClaimConstants.AgreedToTerms, "true"));
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirementAsync_TermsAgreementWithoutClaim_Fails()
    {
        var requirement = new PermissionRequirement(AuthPermissions.TermsAgreement);
        var user = CreateAuthenticatedUser();
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirementAsync_UnauthenticatedUser_DoesNotSucceed()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Access);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = CreateContext(user, requirement);

        await _handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(params Claim[] additionalClaims)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        claims.AddRange(additionalClaims);
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static AuthorizationHandlerContext CreateContext(
        ClaimsPrincipal user,
        IAuthorizationRequirement requirement)
    {
        return new AuthorizationHandlerContext(
            new[] { requirement },
            user,
            resource: null);
    }
}
