using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
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
    private SandboxConfig _config = new();
    private readonly ToolPermissionProfileResolver _resolver;
    private readonly CapabilityEnforcer _enforcer;

    public CapabilityEnforcerSolutionReviewFixTests()
    {
        var configMock = new Mock<IOptionsMonitor<SandboxConfig>>();
        configMock.Setup(m => m.CurrentValue).Returns(() => _config);
        _resolver = new ToolPermissionProfileResolver(configMock.Object);
        _enforcer = new CapabilityEnforcer(
            _resolver,
            Mock.Of<ILogger<CapabilityEnforcer>>());
    }

    [ToolCapability(ToolCapability.FileRead | ToolCapability.FileWrite)]
    private sealed class FileToolType { }

    [Fact]
    public async Task EnforceAsync_SiblingDirectorySharingAllowedPrefix_IsDenied()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./sandbox/work"]
                }
            }
        };

        // Sibling "work-evil" begins with the allowed string "sandbox/work" but is NOT a child.
        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work-evil/loot.txt"]);

        result.IsSuccess.Should().BeFalse(
            "a sibling directory sharing the allowed prefix must not satisfy the allowlist");
    }

    [Fact]
    public async Task EnforceAsync_TrueChildOfAllowedPath_IsPermitted()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./sandbox/work"]
                }
            }
        };

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work/output.txt"]);

        result.IsSuccess.Should().BeTrue(
            "a genuine descendant of the allowed path must still be permitted");
    }

    [Fact]
    public async Task EnforceAsync_AllowedPathItself_IsPermitted()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./sandbox/work"]
                }
            }
        };

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./sandbox/work"]);

        result.IsSuccess.Should().BeTrue(
            "the allowed path boundary itself must be permitted");
    }

    [Fact]
    public async Task EnforceAsync_SiblingOfDeniedPath_IsNotOverDenied()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./data"],
                    DeniedPaths = ["./data/secret"]
                }
            }
        };

        // "secrets-public" shares the prefix "data/secret" but is a sibling of the denied
        // "data/secret" directory and must NOT be over-denied.
        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./data/secrets-public/notes.txt"]);

        result.IsSuccess.Should().BeTrue(
            "a sibling of a denied path that merely shares its prefix must not be denied");
    }

    [Fact]
    public async Task EnforceAsync_TrueChildOfDeniedPath_IsDenied()
    {
        _resolver.RegisterToolType("file_system", typeof(FileToolType));
        _config = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["file_system"] = new ToolOverrideConfig
                {
                    AllowedPaths = ["./data"],
                    DeniedPaths = ["./data/secret"]
                }
            }
        };

        var result = await _enforcer.EnforceAsync(
            "file_system",
            ToolCapability.FileRead | ToolCapability.FileWrite,
            requestedPaths: ["./data/secret/key.pem"]);

        result.IsSuccess.Should().BeFalse(
            "a genuine descendant of a denied path must remain denied");
    }
}
