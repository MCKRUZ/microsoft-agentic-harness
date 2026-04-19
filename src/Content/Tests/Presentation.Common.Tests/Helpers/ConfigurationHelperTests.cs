using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Presentation.Common.Helpers;
using Xunit;

namespace Presentation.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="ConfigurationHelper"/> covering Kestrel URL resolution
/// and config propagation across appsettings files.
/// </summary>
public sealed class ConfigurationHelperTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static IWebHostEnvironment CreateEnvironment(bool isDevelopment)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName)
           .Returns(isDevelopment ? Environments.Development : Environments.Production);
        return env.Object;
    }

    // -- GetKestrelUrl null guards --

    [Fact]
    public void GetKestrelUrl_NullConfiguration_ThrowsArgumentNullException()
    {
        var act = () => ConfigurationHelper.GetKestrelUrl(null!, CreateEnvironment(true));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetKestrelUrl_NullEnvironment_ThrowsArgumentNullException()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => ConfigurationHelper.GetKestrelUrl(config, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- GetKestrelUrl Development defaults --

    [Fact]
    public void GetKestrelUrl_DevelopmentNoConfig_ReturnsHttpDefault()
    {
        var config = new ConfigurationBuilder().Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true));

        url.Should().Be("http://localhost:8001/");
    }

    // -- GetKestrelUrl Production defaults --

    [Fact]
    public void GetKestrelUrl_ProductionNoConfig_ReturnsHttpsDefault()
    {
        var config = new ConfigurationBuilder().Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        url.Should().Be("https://localhost:8001/");
    }

    // -- GetKestrelUrl with configured endpoints --

    [Fact]
    public void GetKestrelUrl_DevelopmentWithHttpEndpoint_ReturnsConfiguredUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://localhost:5000"
            })
            .Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true));

        url.Should().Be("http://localhost:5000");
    }

    [Fact]
    public void GetKestrelUrl_ProductionWithHttpsEndpoint_ReturnsConfiguredUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Https:Url"] = "https://myhost:5001"
            })
            .Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        url.Should().Be("https://myhost:5001");
    }

    // -- GetKestrelUrl wildcard replacement --

    [Fact]
    public void GetKestrelUrl_WildcardBinding_ReplacesWithLocalhost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Https:Url"] = "https://*:5001"
            })
            .Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        url.Should().Be("https://localhost:5001");
    }

    [Fact]
    public void GetKestrelUrl_WildcardBindingEnforceDisabled_KeepsWildcard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5000"
            })
            .Build();

        var url = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true), enforceLocalHost: false);

        url.Should().Be("http://*:5000");
    }

    // -- PropagateAppConfigChanges --

    [Fact]
    public void PropagateAppConfigChanges_NullSourcePath_ThrowsArgumentNullException()
    {
        var act = () => ConfigurationHelper.PropagateAppConfigChanges(null!, []);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PropagateAppConfigChanges_NullTargetPaths_ThrowsArgumentNullException()
    {
        var act = () => ConfigurationHelper.PropagateAppConfigChanges("source.json", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PropagateAppConfigChanges_MissingSourceFile_ThrowsFileNotFoundException()
    {
        var act = () => ConfigurationHelper.PropagateAppConfigChanges(
            Path.Combine(_tempDir, "missing.json"), []);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void PropagateAppConfigChanges_NoAppConfigNode_ThrowsInvalidOperationException()
    {
        var sourcePath = Path.Combine(_tempDir, "source-no-appconfig.json");
        File.WriteAllText(sourcePath, """{"other": "value"}""");

        var act = () => ConfigurationHelper.PropagateAppConfigChanges(sourcePath, []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not contain*appConfig*");
    }

    [Fact]
    public void PropagateAppConfigChanges_ValidSource_CopiesAppConfigToTarget()
    {
        var sourcePath = Path.Combine(_tempDir, "source.json");
        var targetPath = Path.Combine(_tempDir, "target.json");

        File.WriteAllText(sourcePath, """{"appConfig": {"key": "sourceValue"}}""");
        File.WriteAllText(targetPath, """{"appConfig": {"key": "oldValue"}, "other": "keep"}""");

        ConfigurationHelper.PropagateAppConfigChanges(sourcePath, [targetPath]);

        var targetJson = JsonNode.Parse(File.ReadAllText(targetPath))!.AsObject();
        targetJson["appConfig"]!["key"]!.GetValue<string>().Should().Be("sourceValue");
        targetJson["other"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public void PropagateAppConfigChanges_MultipleTargets_AllReceiveUpdate()
    {
        var sourcePath = Path.Combine(_tempDir, "source2.json");
        var target1 = Path.Combine(_tempDir, "target1.json");
        var target2 = Path.Combine(_tempDir, "target2.json");

        File.WriteAllText(sourcePath, """{"appConfig": {"version": "2.0"}}""");
        File.WriteAllText(target1, """{"appConfig": {"version": "1.0"}}""");
        File.WriteAllText(target2, """{"appConfig": {"version": "1.0"}}""");

        ConfigurationHelper.PropagateAppConfigChanges(sourcePath, [target1, target2]);

        var t1 = JsonNode.Parse(File.ReadAllText(target1))!;
        var t2 = JsonNode.Parse(File.ReadAllText(target2))!;
        t1["appConfig"]!["version"]!.GetValue<string>().Should().Be("2.0");
        t2["appConfig"]!["version"]!.GetValue<string>().Should().Be("2.0");
    }
}
