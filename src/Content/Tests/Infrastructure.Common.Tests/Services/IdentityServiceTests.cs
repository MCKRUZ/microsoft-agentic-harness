using FluentAssertions;
using Infrastructure.Common.Services;
using Xunit;

namespace Infrastructure.Common.Tests.Services;

public class IdentityServiceTests
{
    private readonly IdentityService _sut = new();

    [Fact]
    public async Task GetUserNameAsync_ReturnsDevUser()
    {
        var result = await _sut.GetUserNameAsync("any-id");

        result.Should().Be("Development User");
    }

    [Fact]
    public async Task IsInRoleAsync_AlwaysReturnsFalse()
    {
        var result = await _sut.IsInRoleAsync("any-id", "Admin");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CreateUserAsync_ReturnsSuccessWithFixedId()
    {
        var result = await _sut.CreateUserAsync("testuser", "password");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("dev-user-1");
    }
}
