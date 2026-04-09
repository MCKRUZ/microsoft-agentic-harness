using Domain.Common.Enums;
using FluentAssertions;
using Infrastructure.APIAccess.Auth.Providers;
using Infrastructure.APIAccess.Auth.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Auth;

public class PermissionPolicyProviderTests
{
    private readonly PermissionPolicyProvider _provider;

    public PermissionPolicyProviderTests()
    {
        var options = Options.Create(new AuthorizationOptions());
        _provider = new PermissionPolicyProvider(options);
    }

    [Fact]
    public async Task GetPolicyAsync_PermissionPrefixedName_BuildsPolicyWithRequirement()
    {
        var policy = await _provider.GetPolicyAsync("Permission0");

        policy.Should().NotBeNull();

        var permissionRequirement = policy!.Requirements
            .OfType<PermissionRequirement>()
            .Should().ContainSingle()
            .Subject;

        permissionRequirement.Permission.Should().Be(AuthPermissions.Access);

        policy.Requirements
            .OfType<DenyAnonymousAuthorizationRequirement>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task GetPolicyAsync_MultiplePermissions_BuildsPolicyWithAllRequirements()
    {
        var policy = await _provider.GetPolicyAsync("Permission0-1-2");

        policy.Should().NotBeNull();
        var permissionRequirements = policy!.Requirements
            .OfType<PermissionRequirement>()
            .ToList();

        permissionRequirements.Should().HaveCount(3);
        permissionRequirements.Select(r => r.Permission)
            .Should().BeEquivalentTo(new[]
            {
                AuthPermissions.Access,
                AuthPermissions.TermsAgreement,
                AuthPermissions.Admin,
            });
    }

    [Fact]
    public async Task GetPolicyAsync_NonPermissionPolicy_DelegatesToFallback()
    {
        var policy = await _provider.GetPolicyAsync("SomeOtherPolicy");

        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetDefaultPolicyAsync_ReturnsDefaultPolicy()
    {
        var policy = await _provider.GetDefaultPolicyAsync();

        policy.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPolicyAsync_EmptyPermissionSegment_DelegatesToFallback()
    {
        // "Permission" with no suffix should delegate to fallback
        var policy = await _provider.GetPolicyAsync("Permission");

        // Fallback returns null for unregistered policies
        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetPolicyAsync_SingleAdminPermission_CreatesCorrectPolicy()
    {
        var policy = await _provider.GetPolicyAsync("Permission2");

        policy.Should().NotBeNull();

        var permissionRequirement = policy!.Requirements
            .OfType<PermissionRequirement>()
            .Should().ContainSingle()
            .Subject;

        permissionRequirement.Permission.Should().Be(AuthPermissions.Admin);
    }

    [Fact]
    public async Task GetPolicyAsync_InvalidPermissionValue_SkipsInvalidSegment()
    {
        // 999 is not a defined AuthPermissions value, so it should be skipped
        var policy = await _provider.GetPolicyAsync("Permission999");

        policy.Should().NotBeNull();

        // No PermissionRequirement added for undefined enum value
        policy!.Requirements
            .OfType<PermissionRequirement>()
            .Should().BeEmpty();

        // Still requires authenticated user
        policy.Requirements
            .OfType<DenyAnonymousAuthorizationRequirement>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task GetPolicyAsync_MixedValidAndInvalidPermissions_OnlyAddsValid()
    {
        // 0 is valid (Access), 999 is not, 2 is valid (Admin)
        var policy = await _provider.GetPolicyAsync("Permission0-999-2");

        policy.Should().NotBeNull();

        var permissionRequirements = policy!.Requirements
            .OfType<PermissionRequirement>()
            .ToList();

        permissionRequirements.Should().HaveCount(2);
        permissionRequirements.Select(r => r.Permission)
            .Should().BeEquivalentTo(new[]
            {
                AuthPermissions.Access,
                AuthPermissions.Admin,
            });
    }

    [Fact]
    public async Task GetPolicyAsync_NonNumericSegment_SkipsInvalidSegment()
    {
        var policy = await _provider.GetPolicyAsync("Permissionabc");

        policy.Should().NotBeNull();

        policy!.Requirements
            .OfType<PermissionRequirement>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task GetFallbackPolicyAsync_ReturnsNull()
    {
        var policy = await _provider.GetFallbackPolicyAsync();

        // Default AuthorizationOptions does not set a fallback policy
        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetPolicyAsync_AlwaysRequiresAuthenticatedUser()
    {
        var policy = await _provider.GetPolicyAsync("Permission0");

        policy.Should().NotBeNull();
        policy!.Requirements
            .OfType<DenyAnonymousAuthorizationRequirement>()
            .Should().ContainSingle();
    }
}
