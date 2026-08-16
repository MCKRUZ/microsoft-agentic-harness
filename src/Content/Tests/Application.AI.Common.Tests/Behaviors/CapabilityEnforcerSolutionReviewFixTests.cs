using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Behaviors;

/// <summary>
/// Regression tests for the sibling-directory path-confinement bypass in
/// <see cref="CapabilityEnforcer"/> (solution review finding 18). The allowlist previously
/// used a raw <c>string.StartsWith</c> prefix check, so an allowed path "./workspace"
/// admitted sibling directories such as "./workspace-evil". The fix compares on
/// path-segment boundaries instead.
/// </summary>
public sealed class CapabilityEnforcerSolutionReviewFixTests
{
    // "file_system" declares FileRead|FileWrite via ITool.RequiredCapabilities (#387) — mirrors
    // ToolCapabilityResolverTests' pattern for the sibling composition-capability resolver, not the
    // dead [ToolCapabilityAttribute]/RegisterToolType mechanism this replaces.
    private static CapabilityEnforcer BuildEnforcer(SandboxConfig config)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", (_, _) => Mock.Of<ITool>(t =>
            t.RequiredCapabilities == (ToolCapability.FileRead | ToolCapability.FileWrite)));

        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(config);

        var resolver = new ToolPermissionProfileResolver(
            services.BuildServiceProvider(), configMock.Object, new HashSet<string> { "file_system" });
        return new CapabilityEnforcer(resolver, Mock.Of<ILogger<CapabilityEnforcer>>());
    }

    [Fact]
    public async Task EnforceAsync_SiblingDirectorySharingAllowedPrefix_IsDenied()
    {
        var enforcer = BuildEnforcer(new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["./sandbox/work"] }
            }
        });

        // Sibling "work-evil" begins with the allowed string "sandbox/work" but is NOT a child.
        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work-evil/loot.txt"]);

        result.IsSuccess.Should().BeFalse(
            "a sibling directory sharing the allowed prefix must not satisfy the allowlist");
    }

    [Fact]
    public async Task EnforceAsync_TrueChildOfAllowedPath_IsPermitted()
    {
        var enforcer = BuildEnforcer(new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["./sandbox/work"] }
            }
        });

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work/output.txt"]);

        result.IsSuccess.Should().BeTrue(
            "a genuine descendant of the allowed path must still be permitted");
    }

    [Fact]
    public async Task EnforceAsync_AllowedPathItself_IsPermitted()
    {
        var enforcer = BuildEnforcer(new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig { AllowedPaths = ["./sandbox/work"] }
            }
        });

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work"]);

        result.IsSuccess.Should().BeTrue(
            "the allowed path boundary itself must be permitted");
    }

    [Fact]
    public async Task EnforceAsync_SiblingOfDeniedPath_IsNotOverDenied()
    {
        var enforcer = BuildEnforcer(new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./data"],
                    DeniedPaths = ["./data/secret"]
                }
            }
        });

        // "secrets-public" shares the prefix "data/secret" but is a sibling of the denied
        // "data/secret" directory and must NOT be over-denied.
        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./data/secrets-public/notes.txt"]);

        result.IsSuccess.Should().BeTrue(
            "a sibling of a denied path that merely shares its prefix must not be denied");
    }

    [Fact]
    public async Task EnforceAsync_TrueChildOfDeniedPath_IsDenied()
    {
        var enforcer = BuildEnforcer(new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./data"],
                    DeniedPaths = ["./data/secret"]
                }
            }
        });

        var result = await enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./data/secret/key.pem"]);

        result.IsSuccess.Should().BeFalse(
            "a genuine descendant of a denied path must remain denied");
    }
}
