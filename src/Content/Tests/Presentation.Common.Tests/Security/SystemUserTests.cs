using Application.Common.Interfaces.Security;
using FluentAssertions;
using Presentation.Common.Security;
using Xunit;

namespace Presentation.Common.Tests.Security;

/// <summary>
/// Tests for <see cref="SystemUser"/> verifying system identity properties
/// and interface compliance.
/// </summary>
public sealed class SystemUserTests
{
    [Fact]
    public void Id_ReturnsSystem()
    {
        var sut = new SystemUser();

        sut.Id.Should().Be("system");
    }

    [Fact]
    public void IsAdmin_ReturnsTrue()
    {
        var sut = new SystemUser();

        sut.IsAdmin.Should().BeTrue();
    }

    [Fact]
    public void SystemUser_ImplementsIUser()
    {
        var sut = new SystemUser();

        sut.Should().BeAssignableTo<IUser>();
    }

    [Fact]
    public void Id_IsNotNull()
    {
        var sut = new SystemUser();

        sut.Id.Should().NotBeNull();
    }

    [Fact]
    public void MultipleInstances_HaveSameProperties()
    {
        var user1 = new SystemUser();
        var user2 = new SystemUser();

        user1.Id.Should().Be(user2.Id);
        user1.IsAdmin.Should().Be(user2.IsAdmin);
    }
}
