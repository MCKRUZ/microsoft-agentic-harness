using Domain.Common.Constants;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Constants;

/// <summary>
/// Tests for <see cref="ClaimConstants"/> ensuring claim type values are stable.
/// </summary>
public class ClaimConstantsTests
{
    [Fact]
    public void UserId_HasExpectedValue()
    {
        ClaimConstants.UserId.Should().Be("app-user-id");
    }

    [Fact]
    public void IsAdmin_HasExpectedValue()
    {
        ClaimConstants.IsAdmin.Should().Be("app-user-is-admin");
    }

    [Fact]
    public void AgreedToTerms_HasExpectedValue()
    {
        ClaimConstants.AgreedToTerms.Should().Be("app-user-agreed-to-terms");
    }

    [Fact]
    public void AllConstants_AreDistinct()
    {
        var values = new[] { ClaimConstants.UserId, ClaimConstants.IsAdmin, ClaimConstants.AgreedToTerms };

        values.Should().OnlyHaveUniqueItems();
    }
}
