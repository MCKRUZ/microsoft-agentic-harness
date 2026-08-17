using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

/// <summary>
/// Focused coverage for <see cref="DockerContainerLaunchPreparer.ResolveImage"/>'s request-supplied
/// image overload — #371's registration gate needs a bundle-owned session to pick its own runtime
/// image (no per-tool <c>ToolOverrides</c> entry can ever match a bundle's GUID-namespaced name), and
/// that new precedence must not weaken the existing operator image allowlist.
/// </summary>
public class DockerContainerLaunchPreparerTests
{
    private readonly Mock<IOptionsMonitor<SandboxExecutionOptions>> _options = new();
    private readonly DockerContainerLaunchPreparer _sut;

    public DockerContainerLaunchPreparerTests()
    {
        _options.Setup(x => x.CurrentValue).Returns(new SandboxExecutionOptions());
        _sut = new DockerContainerLaunchPreparer(
            Mock.Of<IDockerClient>(), _options.Object, Mock.Of<ILogger<DockerContainerLaunchPreparer>>());
    }

    [Fact]
    public void ResolveImage_RequestImageOutsideAllowedPrefixes_Throws()
    {
        // The default allowlist is mcr.microsoft.com/ only — a caller-specified image cannot use this
        // parameter to escape the operator's registry allowlist, only to pick among permitted images.
        var act = () => _sut.ResolveImage("bundle-owned-tool", "docker.io/attacker/malicious:latest");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not in allowed registry list*");
    }

    [Fact]
    public void ResolveImage_RequestImageWithinAllowedPrefixes_ReturnsIt()
    {
        var image = _sut.ResolveImage("bundle-owned-tool", "mcr.microsoft.com/dotnet/sdk:10.0");

        image.Should().Be("mcr.microsoft.com/dotnet/sdk:10.0");
    }

    [Fact]
    public void ResolveImage_RequestImageTakesPrecedenceOverToolOverride()
    {
        _options.Setup(x => x.CurrentValue).Returns(new SandboxExecutionOptions
        {
            ToolOverrides = new Dictionary<string, ToolSandboxOverride>
            {
                ["bundle-owned-tool"] = new() { ContainerImage = "mcr.microsoft.com/dotnet/aspnet:10.0" },
            },
        });

        var image = _sut.ResolveImage("bundle-owned-tool", "mcr.microsoft.com/dotnet/sdk:10.0");

        image.Should().Be("mcr.microsoft.com/dotnet/sdk:10.0",
            "an explicit request image is the caller's own instruction for THIS session and must win");
    }

    [Fact]
    public void ResolveImage_NoRequestImage_FallsBackToToolOverrideThenDefault()
    {
        var image = _sut.ResolveImage("unconfigured-tool");

        image.Should().Be("mcr.microsoft.com/dotnet/runtime:10.0", "unchanged pre-#371 fallback behavior");
    }
}
