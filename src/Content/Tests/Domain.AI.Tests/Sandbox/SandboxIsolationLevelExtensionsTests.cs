using Domain.AI.Sandbox;
using Xunit;

namespace Domain.AI.Tests.Sandbox;

/// <summary>
/// Guards the shared isolation-floor helpers extracted from four independently-duplicated call
/// sites (#433): <see cref="SandboxIsolationLevelExtensions.AtLeast"/> and
/// <see cref="SandboxIsolationLevelExtensions.WithMinimumIsolationAtLeast"/>.
/// </summary>
public sealed class SandboxIsolationLevelExtensionsTests
{
    [Theory]
    [InlineData(SandboxIsolationLevel.None, SandboxIsolationLevel.None, SandboxIsolationLevel.None)]
    [InlineData(SandboxIsolationLevel.None, SandboxIsolationLevel.Process, SandboxIsolationLevel.Process)]
    [InlineData(SandboxIsolationLevel.Process, SandboxIsolationLevel.None, SandboxIsolationLevel.Process)]
    [InlineData(SandboxIsolationLevel.Process, SandboxIsolationLevel.Container, SandboxIsolationLevel.Container)]
    [InlineData(SandboxIsolationLevel.Container, SandboxIsolationLevel.Process, SandboxIsolationLevel.Container)]
    [InlineData(SandboxIsolationLevel.Container, SandboxIsolationLevel.Container, SandboxIsolationLevel.Container)]
    public void AtLeast_ReturnsTheStricterLevel(
        SandboxIsolationLevel level, SandboxIsolationLevel floor, SandboxIsolationLevel expected)
    {
        Assert.Equal(expected, level.AtLeast(floor));
    }

    [Fact]
    public void WithMinimumIsolationAtLeast_AlreadyAtFloor_ReturnsSameInstance()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            MinimumIsolation = SandboxIsolationLevel.Container
        };

        var result = profile.WithMinimumIsolationAtLeast(SandboxIsolationLevel.Process);

        Assert.Same(profile, result);
    }

    [Fact]
    public void WithMinimumIsolationAtLeast_AboveFloor_ReturnsSameInstance()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            MinimumIsolation = SandboxIsolationLevel.Container
        };

        var result = profile.WithMinimumIsolationAtLeast(SandboxIsolationLevel.None);

        Assert.Same(profile, result);
    }

    [Fact]
    public void WithMinimumIsolationAtLeast_BelowFloor_RaisesMinimumIsolationAndPreservesOtherFields()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.FileWrite,
            DeniedCapabilities = ToolCapability.NetworkAccess,
            AllowedPrograms = ["dotnet"],
            MinimumIsolation = SandboxIsolationLevel.Process
        };

        var result = profile.WithMinimumIsolationAtLeast(SandboxIsolationLevel.Container);

        Assert.NotSame(profile, result);
        Assert.Equal(SandboxIsolationLevel.Container, result.MinimumIsolation);
        Assert.Equal(ToolCapability.FileWrite, result.RequiredCapabilities);
        Assert.Equal(ToolCapability.NetworkAccess, result.DeniedCapabilities);
        Assert.Equal(profile.AllowedPrograms, result.AllowedPrograms);
    }
}
