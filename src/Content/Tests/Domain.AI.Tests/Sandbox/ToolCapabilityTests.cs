using Domain.AI.Sandbox;
using Xunit;

namespace Domain.AI.Tests.Sandbox;

public sealed class ToolCapabilityTests
{
    [Fact]
    public void ToolCapability_Flags_CanCombineMultiple()
    {
        var combined = ToolCapability.FileRead | ToolCapability.NetworkAccess;

        Assert.True(combined.HasFlag(ToolCapability.FileRead));
        Assert.True(combined.HasFlag(ToolCapability.NetworkAccess));
        Assert.False(combined.HasFlag(ToolCapability.FileWrite));
    }

    [Fact]
    public void ToolCapability_BitwiseAnd_DetectsMissingCapabilities()
    {
        var required = ToolCapability.FileRead | ToolCapability.FileWrite | ToolCapability.NetworkAccess;
        var granted = ToolCapability.FileRead | ToolCapability.FileWrite;

        var missing = required & ~granted;

        Assert.Equal(ToolCapability.NetworkAccess, missing);
    }

    [Fact]
    public void ToolPermissionProfile_EffectiveCapabilities_SubtractsDeniedFromRequired()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.FileRead | ToolCapability.NetworkAccess,
            DeniedCapabilities = ToolCapability.NetworkAccess
        };

        // RequiredCapabilities stays the tool's undiminished declaration (#405) — only
        // EffectiveCapabilities, the value sandbox provisioning reads, is narrowed.
        Assert.Equal(ToolCapability.FileRead | ToolCapability.NetworkAccess, profile.RequiredCapabilities);
        Assert.Equal(ToolCapability.FileRead, profile.EffectiveCapabilities);
    }

    [Fact]
    public void ToolPermissionProfile_EffectiveCapabilities_NoDeny_EqualsRequired()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.FileRead | ToolCapability.FileWrite
        };

        Assert.Equal(profile.RequiredCapabilities, profile.EffectiveCapabilities);
    }

    [Fact]
    public void SandboxIsolationLevel_Ordering_ContainerHigherThanProcess()
    {
        Assert.True((int)SandboxIsolationLevel.Container > (int)SandboxIsolationLevel.Process);
        Assert.True((int)SandboxIsolationLevel.Process > (int)SandboxIsolationLevel.None);
    }
}
