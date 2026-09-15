using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Config;

/// <summary>
/// Tests for <see cref="SandboxConfig.ToolOverrides"/>'s case-insensitive <see langword="init"/>
/// accessor (#655 altitude follow-up) — a type-level guarantee tested directly, independent of any
/// particular consumer.
/// </summary>
public class SandboxConfigToolOverridesTests
{
    [Fact]
    public void DefaultInstance_MutatedAfterConstruction_IsCaseInsensitive()
    {
        var config = new SandboxConfig();
        config.ToolOverrides["BASH"] = new ToolOverrideConfig();

        config.ToolOverrides.TryGetValue("bash", out _).Should().BeTrue();
    }

    [Fact]
    public void ObjectInitializerReplacesTheDictionary_StillRebuildsWithCaseInsensitiveComparer()
    {
        // The gap the plain `= new(StringComparer.OrdinalIgnoreCase)` default value alone would miss:
        // an object-initializer assigns a BRAND NEW dictionary, never inheriting this property's
        // default value or its comparer, unless the init accessor itself rebuilds what it's given.
        var config = new SandboxConfig
        {
            ToolOverrides = new() { ["BASH"] = new ToolOverrideConfig() },
        };

        config.ToolOverrides.TryGetValue("bash", out _).Should().BeTrue();
    }

    [Fact]
    public void ObjectInitializer_TwoSourceKeysCollideUnderTheNewComparer_ThrowsRatherThanSilentlyPickingOne()
    {
        var act = () => new SandboxConfig
        {
            ToolOverrides = new(StringComparer.Ordinal)
            {
                ["bash"] = new ToolOverrideConfig(),
                ["BASH"] = new ToolOverrideConfig(),
            },
        };

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Tests for <see cref="SandboxOptions.ToolOverrides"/>'s case-insensitive <see langword="set"/>
/// accessor — the same fix as <see cref="SandboxConfigToolOverridesTests"/>, applied to a sibling
/// dictionary of the identical shape found unfixed during #655's round-2 correctness-review.
/// </summary>
public class SandboxOptionsToolOverridesTests
{
    [Fact]
    public void ObjectInitializerReplacesTheDictionary_StillRebuildsWithCaseInsensitiveComparer()
    {
        var options = new SandboxOptions
        {
            ToolOverrides = new() { ["BASH"] = new ToolOverrideConfig() },
        };

        options.ToolOverrides.TryGetValue("bash", out _).Should().BeTrue();
    }

    [Fact]
    public void ObjectInitializer_TwoSourceKeysCollideUnderTheNewComparer_ThrowsRatherThanSilentlyPickingOne()
    {
        var act = () => new SandboxOptions
        {
            ToolOverrides = new(StringComparer.Ordinal)
            {
                ["bash"] = new ToolOverrideConfig(),
                ["BASH"] = new ToolOverrideConfig(),
            },
        };

        act.Should().Throw<ArgumentException>();
    }
}
