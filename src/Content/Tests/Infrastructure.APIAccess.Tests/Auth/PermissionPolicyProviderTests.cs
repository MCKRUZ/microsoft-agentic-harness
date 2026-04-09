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
        // "Permission0" maps to AuthPermissions.Access (value 0)
        var policy = await _provider.GetPolicyAsync("Permission0");

        policy.Should().NotBeNull();

        var permissionRequirement = policy!.Requirements
            .OfType<PermissionRequirement>()
            .Should().ContainSingle()
            .Subject;

        permissionRequirement.Permission.Should().Be(AuthPermissions.Access);

        // Also requires authenticated user
        policy.Requirements
            .OfType<DenyAnonymousAuthorizationRequirement>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task GetPolicyAsync_MultiplePermissions_BuildsPolicyWithAllRequirements()
    {
        // "Permission0-1-2" maps to Access, TermsAgreement, Admin
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

        // Fallback returns null for unregistered policies
        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetDefaultPolicyAsync_ReturnsDefaultPolicy()
    {
        var policy = await _provider.GetDefaultPolicyAsync();

        policy.Should().NotBeNull();
    }
}
