using Domain.Common.Enums;
using FluentAssertions;
using Infrastructure.APIAccess.Auth.Providers;
using Infrastructure.APIAccess.Auth.Requirements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.APIAccess.Tests.Auth;

/// <summary>
/// Integration tests for <see cref="PermissionPolicyProvider"/> covering dynamic
/// policy resolution from permission-encoded policy names.
/// </summary>
public sealed class PermissionPolicyProviderIntegrationTests
{
    private static PermissionPolicyProvider CreateProvider()
    {
        var options = Options.Create(new AuthorizationOptions());
        return new PermissionPolicyProvider(options);
    }

    [Fact]
    public async Task GetPolicyAsync_SinglePermission_BuildsPolicyWithOneRequirement()
    {
        var sut = CreateProvider();
        var accessInt = (int)AuthPermissions.Access;

        var policy = await sut.GetPolicyAsync($"Permission{accessInt}");

        policy.Should().NotBeNull();
        policy!.Requirements.Should().ContainSingle(r => r is PermissionRequirement);
        var req = policy.Requirements.OfType<PermissionRequirement>().Single();
        req.Permission.Should().Be(AuthPermissions.Access);
    }

    [Fact]
    public async Task GetPolicyAsync_MultiplePermissions_BuildsPolicyWithMultipleRequirements()
    {
        var sut = CreateProvider();
        var accessInt = (int)AuthPermissions.Access;
        var adminInt = (int)AuthPermissions.Admin;

        var policy = await sut.GetPolicyAsync($"Permission{accessInt}-{adminInt}");

        policy.Should().NotBeNull();
        var requirements = policy!.Requirements.OfType<PermissionRequirement>().ToList();
        requirements.Should().HaveCount(2);
        requirements.Select(r => r.Permission).Should()
            .Contain(AuthPermissions.Access)
            .And.Contain(AuthPermissions.Admin);
    }

    [Fact]
    public async Task GetPolicyAsync_RequiresAuthenticatedUser()
    {
        var sut = CreateProvider();
        var accessInt = (int)AuthPermissions.Access;

        var policy = await sut.GetPolicyAsync($"Permission{accessInt}");

        policy.Should().NotBeNull();
        policy!.Requirements.Should().Contain(r =>
            r.GetType().Name == "DenyAnonymousAuthorizationRequirement");
    }

    [Fact]
    public async Task GetPolicyAsync_NonPermissionPolicy_DelegatesToFallback()
    {
        var sut = CreateProvider();

        var policy = await sut.GetPolicyAsync("SomeOtherPolicy");

        // Default provider returns null for unknown policies
        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetPolicyAsync_EmptyPermissionSuffix_DelegatesToFallback()
    {
        var sut = CreateProvider();

        var policy = await sut.GetPolicyAsync("Permission");

        // Empty suffix delegates to fallback
        policy.Should().BeNull();
    }

    [Fact]
    public async Task GetPolicyAsync_InvalidPermissionValues_SkipsInvalidSegments()
    {
        var sut = CreateProvider();
        var accessInt = (int)AuthPermissions.Access;

        var policy = await sut.GetPolicyAsync($"Permission{accessInt}-999-abc");

        policy.Should().NotBeNull();
        // Only the valid permission (Access) should be added
        var requirements = policy!.Requirements.OfType<PermissionRequirement>().ToList();
        requirements.Should().ContainSingle();
        requirements[0].Permission.Should().Be(AuthPermissions.Access);
    }

    [Fact]
    public async Task GetDefaultPolicyAsync_ReturnsDefaultPolicy()
    {
        var sut = CreateProvider();

        var policy = await sut.GetDefaultPolicyAsync();

        policy.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFallbackPolicyAsync_ReturnsNull()
    {
        var sut = CreateProvider();

        var policy = await sut.GetFallbackPolicyAsync();

        policy.Should().BeNull();
    }
}
