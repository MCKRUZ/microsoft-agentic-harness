using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Permissions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

public sealed class SafetyGateRegistryTests
{
    private SafetyGateRegistry CreateRegistry(params string[] gatePaths)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = new PermissionsConfig
                {
                    SafetyGatePaths = gatePaths
                }
            }
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        return new SafetyGateRegistry(monitor.Object);
    }

    [Fact]
    public void Gate_TriggeredForGitPath()
    {
        var registry = CreateRegistry(".git/", ".ssh/", ".env");
        var parameters = new Dictionary<string, object?> { ["path"] = "/repo/.git/config" };

        var gate = registry.CheckSafetyGate("file_system", parameters);

        gate.Should().NotBeNull();
        gate!.PathPattern.Should().Be(".git/");
    }

    [Fact]
    public void Gate_TriggeredForSshPath()
    {
        var registry = CreateRegistry(".git/", ".ssh/", ".env");
        var parameters = new Dictionary<string, object?> { ["file_path"] = "C:\\Users\\user\\.ssh\\id_rsa" };

        var gate = registry.CheckSafetyGate("file_system", parameters);

        gate.Should().NotBeNull();
        gate!.PathPattern.Should().Be(".ssh/");
    }

    [Fact]
    public void Gate_NotTriggeredForSafePath()
    {
        var registry = CreateRegistry(".git/", ".ssh/", ".env");
        var parameters = new Dictionary<string, object?> { ["path"] = "/repo/src/main.cs" };

        var gate = registry.CheckSafetyGate("file_system", parameters);

        gate.Should().BeNull();
    }

    [Fact]
    public void Gate_ChecksMultipleParameterKeys()
    {
        var registry = CreateRegistry(".git/", ".ssh/", ".env");
        var parameters = new Dictionary<string, object?>
        {
            ["path"] = "/safe/file.txt",
            ["directory"] = "/repo/.git/hooks"
        };

        var gate = registry.CheckSafetyGate("file_system", parameters);

        gate.Should().NotBeNull();
        gate!.PathPattern.Should().Be(".git/");
    }

    [Fact]
    public void Gate_NullParameters_ReturnsNull()
    {
        var registry = CreateRegistry(".git/");

        var gate = registry.CheckSafetyGate("file_system", null);

        gate.Should().BeNull();
    }

    [Fact]
    public void Gate_EmptyParameters_ReturnsNull()
    {
        var registry = CreateRegistry(".git/");
        var parameters = new Dictionary<string, object?>();

        var gate = registry.CheckSafetyGate("file_system", parameters);

        gate.Should().BeNull();
    }

    [Fact]
    public void Gates_ReturnsConfiguredGates()
    {
        var registry = CreateRegistry(".git/", ".ssh/");

        registry.Gates.Should().HaveCount(2);
        registry.Gates.Should().Contain(g => g.PathPattern == ".git/");
        registry.Gates.Should().Contain(g => g.PathPattern == ".ssh/");
    }
}
