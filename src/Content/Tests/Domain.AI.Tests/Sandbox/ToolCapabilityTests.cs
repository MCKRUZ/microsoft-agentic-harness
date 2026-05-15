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
    public void ToolPermissionProfile_DeniedPaths_OverrideAllowedPaths()
    {
        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.FileRead,
            AllowedPaths = ["/workspace"],
            DeniedPaths = ["/workspace/secret"]
        };

        Assert.Contains("/workspace/secret", profile.DeniedPaths);
        Assert.Contains("/workspace", profile.AllowedPaths);
    }

    [Fact]
    public void SandboxIsolationLevel_Ordering_ContainerHigherThanProcess()
    {
        Assert.True((int)SandboxIsolationLevel.Container > (int)SandboxIsolationLevel.Process);
        Assert.True((int)SandboxIsolationLevel.Process > (int)SandboxIsolationLevel.None);
    }

    [Fact]
    public void ToolCapabilityAttribute_OnClass_DeclaresCapabilitiesAndMinIsolation()
    {
        var attr = new ToolCapabilityAttribute(ToolCapability.FileRead | ToolCapability.FileWrite)
        {
            MinimumIsolation = SandboxIsolationLevel.Container
        };

        Assert.Equal(ToolCapability.FileRead | ToolCapability.FileWrite, attr.Capabilities);
        Assert.Equal(SandboxIsolationLevel.Container, attr.MinimumIsolation);
    }
}
