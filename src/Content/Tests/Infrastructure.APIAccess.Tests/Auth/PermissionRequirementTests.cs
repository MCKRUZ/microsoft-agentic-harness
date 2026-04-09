using Domain.Common.Enums;
using FluentAssertions;
using Infrastructure.APIAccess.Auth.Requirements;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Auth;

public class PermissionRequirementTests
{
    [Theory]
    [InlineData(AuthPermissions.Access)]
    [InlineData(AuthPermissions.TermsAgreement)]
    [InlineData(AuthPermissions.Admin)]
    public void Constructor_SetsPermission(AuthPermissions permission)
    {
        var requirement = new PermissionRequirement(permission);

        requirement.Permission.Should().Be(permission);
    }

    [Fact]
    public void Permission_ReturnsValuePassedToConstructor()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Admin);

        requirement.Permission.Should().Be(AuthPermissions.Admin);
    }

    [Fact]
    public void Requirement_ImplementsIAuthorizationRequirement()
    {
        var requirement = new PermissionRequirement(AuthPermissions.Access);

        requirement.Should().BeAssignableTo<Microsoft.AspNetCore.Authorization.IAuthorizationRequirement>();
    }
}
