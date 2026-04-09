using System.Security.Claims;
using Domain.Common.Constants;
using FluentAssertions;
using Infrastructure.Common.Extensions;
using Xunit;

namespace Infrastructure.Common.Tests.Extensions;

public class ClaimExtensionsTests
{
    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void GetUserId_WithClaim_ReturnsValue()
    {
        var principal = CreatePrincipal(new Claim(ClaimConstants.UserId, "user-42"));

        var result = principal.GetUserId();

        result.Should().Be("user-42");
    }

    [Fact]
    public void GetUserId_WithoutClaim_ReturnsNull()
    {
        var principal = CreatePrincipal();

        var result = principal.GetUserId();

        result.Should().BeNull();
    }

    [Fact]
    public void GetUserId_WithEmptyValue_ReturnsNull()
    {
        var principal = CreatePrincipal(new Claim(ClaimConstants.UserId, ""));

        var result = principal.GetUserId();

        result.Should().BeNull();
    }

    [Fact]
    public void IsAdmin_TrueClaim_ReturnsTrue()
    {
        var principal = CreatePrincipal(new Claim(ClaimConstants.IsAdmin, "true"));

        var result = principal.IsAdmin();

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_MissingClaim_ReturnsFalse()
    {
        var principal = CreatePrincipal();

        var result = principal.IsAdmin();

        result.Should().BeFalse();
    }

    [Fact]
    public void HasAgreedToTerms_TrueClaim_ReturnsTrue()
    {
        var principal = CreatePrincipal(new Claim(ClaimConstants.AgreedToTerms, "true"));

        var result = principal.HasAgreedToTerms();

        result.Should().BeTrue();
    }

    [Fact]
    public void HasAgreedToTerms_FalseClaim_ReturnsFalse()
    {
        var principal = CreatePrincipal(new Claim(ClaimConstants.AgreedToTerms, "false"));

        var result = principal.HasAgreedToTerms();

        result.Should().BeFalse();
    }
}
