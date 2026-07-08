using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Presentation.AgentHub.Tests.Configuration;

/// <summary>
/// Security guard tests pinning a safe out-of-box host posture in the SHIPPED base
/// <c>appsettings.json</c> of every host. These files are what enterprise consumers clone and
/// run first; a wildcard Kestrel bind or a repository-root file-system sandbox turns a plain
/// <c>dotnet run</c> into an unauthenticated agent reachable from the LAN with read access to the
/// whole repo. The guard fails the build if either regresses.
/// </summary>
/// <remarks>
/// The base config is asserted deliberately (no environment overlay). Broad local-exploration
/// access is allowed to live in <c>appsettings.Development.json</c> — never in the base default.
/// </remarks>
public sealed class SafeDefaultHostPostureTests
{
    private const string AllowedBasePathsKey =
        "AppConfig:Infrastructure:FileSystem:AllowedBasePaths";

    private static IConfigurationRoot LoadBaseConfig(string linkedFileName) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "host-config", linkedFileName),
                optional: false)
            .Build();

    [Fact]
    public void AgentHubBaseConfig_KestrelBindsLocalhostOnly_NotWildcard()
    {
        var config = LoadBaseConfig("agenthub.appsettings.json");

        var httpUrl = config["Kestrel:Endpoints:Http:Url"];
        var httpsUrl = config["Kestrel:Endpoints:Https:Url"];

        httpUrl.Should().NotBeNullOrWhiteSpace();
        AssertLoopbackBind(httpUrl!);
        if (!string.IsNullOrWhiteSpace(httpsUrl))
            AssertLoopbackBind(httpsUrl!);
    }

    [Theory]
    [InlineData("agenthub.appsettings.json")]
    [InlineData("consoleui.appsettings.json")]
    [InlineData("evalrunner.appsettings.json")]
    [InlineData("foundryhost.appsettings.json")]
    public void HostBaseConfig_FileSystemBasePath_IsScoped_NotRepositoryRoot(string linkedFileName)
    {
        var config = LoadBaseConfig(linkedFileName);

        var basePaths = config.GetSection(AllowedBasePathsKey).Get<string[]>();

        basePaths.Should().NotBeNullOrEmpty(
            "every host must scope the file-system tool to an explicit base path");
        basePaths.Should().OnlyContain(
            p => !p.Contains("..", StringComparison.Ordinal),
            "a parent-directory traversal ('..') escapes the app content root toward the repository root");
    }

    private static void AssertLoopbackBind(string url)
    {
        url.Should().NotContain("*",
            "a wildcard host binds every network interface, exposing the agent on the LAN");
        url.Should().NotContain("0.0.0.0",
            "0.0.0.0 binds every network interface, exposing the agent on the LAN");
        url.Should().NotContain("+",
            "'+' binds every network interface (strong wildcard), exposing the agent on the LAN");
        (url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || url.Contains("127.0.0.1", StringComparison.Ordinal))
            .Should().BeTrue("the shipped default must bind loopback only");
    }
}
