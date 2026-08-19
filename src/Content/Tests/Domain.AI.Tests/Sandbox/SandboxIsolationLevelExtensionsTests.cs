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
    public void SandboxIsolationLevel_DeclaredValues_AreStrictlyAscendingInIsolationStrength()
    {
        // security-review follow-up on PR #443: AtLeast relies entirely on the enum's declared
        // numeric values being strictly ascending in isolation strength (its own remarks say so).
        // Every isolation-elevation call site in the sandbox subsystem now funnels through this one
        // method, so a future member added out of order (e.g. a weaker tier given a higher value
        // than Container) would silently invert every floor merge at once. This pins that ordering
        // invariant directly.
        //
        // Enum.GetValues() itself returns members already sorted by numeric value, so it cannot be
        // used to detect a declaration-order/value-order mismatch — reflection over the fields in
        // their declared source order is required to catch that.
        var declaredValues = typeof(SandboxIsolationLevel)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (int)f.GetValue(null)!)
            .ToList();

        var ascending = declaredValues.OrderBy(v => v).ToList();
        Assert.Equal(ascending, declaredValues);
        Assert.Equal(declaredValues.Distinct().Count(), declaredValues.Count);
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
