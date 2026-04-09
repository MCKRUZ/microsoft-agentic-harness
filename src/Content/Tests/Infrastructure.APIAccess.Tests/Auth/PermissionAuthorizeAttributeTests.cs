using Domain.Common.Enums;
using FluentAssertions;
using Infrastructure.APIAccess.Auth.Attributes;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Auth;

public class PermissionAuthorizeAttributeTests
{
    [Fact]
    public void Constructor_SinglePermission_GeneratesCorrectPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(AuthPermissions.Access);

        attribute.Policy.Should().Be("Permission0");
    }

    [Fact]
    public void Constructor_AdminPermission_GeneratesCorrectPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(AuthPermissions.Admin);

        attribute.Policy.Should().Be("Permission2");
    }

    [Fact]
    public void Constructor_MultiplePermissions_GeneratesHyphenatedPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(
            AuthPermissions.Access, AuthPermissions.Admin);

        attribute.Policy.Should().Be("Permission0-2");
    }

    [Fact]
    public void Constructor_AllPermissions_GeneratesCorrectPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(
            AuthPermissions.Access,
            AuthPermissions.TermsAgreement,
            AuthPermissions.Admin);

        attribute.Policy.Should().Be("Permission0-1-2");
    }

    [Fact]
    public void Constructor_NoPermissions_GeneratesEmptyPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute();

        attribute.Policy.Should().Be("Permission");
    }

    [Fact]
    public void Constructor_NullPermissions_GeneratesEmptyPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(null!);

        attribute.Policy.Should().Be("Permission");
    }

    [Fact]
    public void Constructor_DuplicatePermissions_IncludesBothInPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(
            AuthPermissions.Admin, AuthPermissions.Admin);

        attribute.Policy.Should().Be("Permission2-2");
    }

    [Fact]
    public void Constructor_TermsAgreementPermission_GeneratesCorrectPolicy()
    {
        var attribute = new PermissionAuthorizeAttribute(AuthPermissions.TermsAgreement);

        attribute.Policy.Should().Be("Permission1");
    }
}
