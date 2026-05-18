using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Infrastructure.AI.Tests.Configuration;

public sealed class SandboxOptionsTests
{
    [Fact]
    public void SandboxOptions_Defaults_CorrectValues()
    {
        var options = new SandboxOptions();

        options.Enabled.Should().BeTrue();
        options.DefaultIsolationLevel.Should().Be("Process");
        options.DefaultMemoryLimitMb.Should().Be(256);
        options.DefaultCpuTimeSeconds.Should().Be(30);
        options.DefaultMaxSubprocesses.Should().Be(5);
        options.DefaultDiskQuotaMb.Should().Be(100);
        options.DefaultTimeoutSeconds.Should().Be(60);
        options.ContainerDefaults.Should().NotBeNull();
        options.ContainerDefaults.DefaultImage.Should().Be("mcr.microsoft.com/dotnet/runtime:10.0-alpine");
        options.ContainerDefaults.NetworkMode.Should().Be("none");
        options.ContainerDefaults.ReadonlyRootfs.Should().BeTrue();
        options.ContainerDefaults.AutoRemove.Should().BeTrue();
        options.ContainerDefaults.WorkspaceMountPath.Should().Be("/workspace");
        options.ContainerDefaults.KillGracePeriodSeconds.Should().Be(10);
        options.ToolOverrides.Should().BeEmpty();
    }

    [Fact]
    public void SandboxOptions_Binding_ReadsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Sandbox:Enabled"] = "false",
                ["AppConfig:AI:Sandbox:DefaultIsolationLevel"] = "Container",
                ["AppConfig:AI:Sandbox:DefaultMemoryLimitMb"] = "512",
                ["AppConfig:AI:Sandbox:DefaultCpuTimeSeconds"] = "60",
                ["AppConfig:AI:Sandbox:DefaultMaxSubprocesses"] = "10",
                ["AppConfig:AI:Sandbox:DefaultDiskQuotaMb"] = "200",
                ["AppConfig:AI:Sandbox:DefaultTimeoutSeconds"] = "120",
                ["AppConfig:AI:Sandbox:ContainerDefaults:DefaultImage"] = "ubuntu:22.04",
                ["AppConfig:AI:Sandbox:ContainerDefaults:NetworkMode"] = "bridge",
                ["AppConfig:AI:Sandbox:ContainerDefaults:ReadonlyRootfs"] = "false",
                ["AppConfig:AI:Sandbox:ContainerDefaults:AutoRemove"] = "false",
                ["AppConfig:AI:Sandbox:ContainerDefaults:WorkspaceMountPath"] = "/app",
                ["AppConfig:AI:Sandbox:ContainerDefaults:KillGracePeriodSeconds"] = "30"
            })
            .Build();

        var options = config.GetSection("AppConfig:AI:Sandbox").Get<SandboxOptions>()!;

        options.Enabled.Should().BeFalse();
        options.DefaultIsolationLevel.Should().Be("Container");
        options.DefaultMemoryLimitMb.Should().Be(512);
        options.DefaultCpuTimeSeconds.Should().Be(60);
        options.DefaultMaxSubprocesses.Should().Be(10);
        options.DefaultDiskQuotaMb.Should().Be(200);
        options.DefaultTimeoutSeconds.Should().Be(120);
        options.ContainerDefaults.DefaultImage.Should().Be("ubuntu:22.04");
        options.ContainerDefaults.NetworkMode.Should().Be("bridge");
        options.ContainerDefaults.ReadonlyRootfs.Should().BeFalse();
        options.ContainerDefaults.AutoRemove.Should().BeFalse();
        options.ContainerDefaults.WorkspaceMountPath.Should().Be("/app");
        options.ContainerDefaults.KillGracePeriodSeconds.Should().Be(30);
    }

    [Fact]
    public void SandboxOptions_ToolOverrides_ParsedCorrectly()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:DeniedCapabilities:0"] = "NetworkAccess",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:DeniedCapabilities:1"] = "Subprocess",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:AllowedPaths:0"] = "./workspace",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:AllowedPaths:1"] = "/tmp",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:MinimumIsolation"] = "Process",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:MemoryLimitMb"] = "128",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:CpuTimeSeconds"] = "15",
                ["AppConfig:AI:Sandbox:ToolOverrides:file_system:TimeoutSeconds"] = "30"
            })
            .Build();

        var options = config.GetSection("AppConfig:AI:Sandbox").Get<SandboxOptions>()!;

        options.ToolOverrides.Should().ContainKey("file_system");
        var fileSystem = options.ToolOverrides["file_system"];
        fileSystem.DeniedCapabilities.Should().Contain("NetworkAccess").And.Contain("Subprocess");
        fileSystem.AllowedPaths.Should().Contain("./workspace").And.Contain("/tmp");
        fileSystem.MinimumIsolation.Should().Be("Process");
        fileSystem.MemoryLimitMb.Should().Be(128);
        fileSystem.CpuTimeSeconds.Should().Be(15);
        fileSystem.TimeoutSeconds.Should().Be(30);
    }
}
