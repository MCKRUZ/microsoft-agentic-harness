using Application.AI.Common.Models.Sandbox;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Models.Sandbox;

/// <summary>
/// Tests for <see cref="SandboxExecutionOptions.ToolOverrides"/>'s case-insensitive
/// <see langword="init"/> accessor — the sibling gap to
/// <c>Domain.Common.Config.AI.Sandbox.SandboxConfig.ToolOverrides</c> found during #655's altitude
/// review, fixed identically.
/// </summary>
public class SandboxExecutionOptionsTests
{
    [Fact]
    public void ObjectInitializerReplacesTheDictionary_StillRebuildsWithCaseInsensitiveComparer()
    {
        var options = new SandboxExecutionOptions
        {
            ToolOverrides = new Dictionary<string, ToolSandboxOverride>
            {
                ["BASH"] = new() { ContainerImage = "mcr.microsoft.com/dotnet/aspnet:10.0" },
            },
        };

        options.ToolOverrides.TryGetValue("bash", out _).Should().BeTrue();
    }

    [Fact]
    public void ObjectInitializer_TwoSourceKeysCollideUnderTheNewComparer_ThrowsRatherThanSilentlyPickingOne()
    {
        var sourceDictionary = new Dictionary<string, ToolSandboxOverride>(StringComparer.Ordinal)
        {
            ["bash"] = new(),
            ["BASH"] = new(),
        };

        var act = () => new SandboxExecutionOptions { ToolOverrides = sourceDictionary };

        act.Should().Throw<ArgumentException>();
    }
}
