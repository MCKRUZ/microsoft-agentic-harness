using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;
using Presentation.Common.Helpers;
using Xunit;

namespace Presentation.Common.Tests.Helpers;

/// <summary>
/// Integration tests for <see cref="ConfigurationHelper"/> covering Kestrel URL
/// resolution and config propagation with real files.
/// </summary>
public sealed class ConfigurationHelperIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationHelperIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-tests-{Guid.NewGuid():N}");
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
        env.Setup(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        env.Setup(e => e.ContentRootFileProvider).Returns(new NullFileProvider());
        env.Setup(e => e.WebRootFileProvider).Returns(new NullFileProvider());
        return env.Object;
    }

    // -- GetKestrelUrl --

    [Fact]
    public void GetKestrelUrl_Development_ReturnsHttpUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://localhost:5000"
            })
            .Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true));

        result.Should().Be("http://localhost:5000");
    }

    [Fact]
    public void GetKestrelUrl_Production_ReturnsHttpsUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Https:Url"] = "https://localhost:5001"
            })
            .Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        result.Should().Be("https://localhost:5001");
    }

    [Fact]
    public void GetKestrelUrl_NoConfig_ReturnsDevelopmentDefault()
    {
        var config = new ConfigurationBuilder().Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true));

        result.Should().Be("http://localhost:8001/");
    }

    [Fact]
    public void GetKestrelUrl_NoConfig_ReturnsProductionDefault()
    {
        var config = new ConfigurationBuilder().Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        result.Should().Be("https://localhost:8001/");
    }

    [Fact]
    public void GetKestrelUrl_WildcardBinding_ReplacedWithLocalhost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5000"
            })
            .Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(true));

        result.Should().Be("http://localhost:5000");
    }

    [Fact]
    public void GetKestrelUrl_WildcardBindingWithEnforceDisabled_KeepsWildcard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://*:5000"
            })
            .Build();

        var result = ConfigurationHelper.GetKestrelUrl(
            config, CreateEnvironment(true), enforceLocalHost: false);

        result.Should().Be("http://*:5000");
    }

    [Fact]
    public void GetKestrelUrl_HttpsWildcard_ReplacedWithLocalhost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Https:Url"] = "https://*:5001/"
            })
            .Build();

        var result = ConfigurationHelper.GetKestrelUrl(config, CreateEnvironment(false));

        result.Should().Be("https://localhost:5001/");
    }

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

    // -- PropagateAppConfigChanges --

    [Fact]
    public void PropagateAppConfigChanges_CopiesAppConfigToTarget()
    {
        var sourceFile = Path.Combine(_tempDir, "source.json");
        var targetFile = Path.Combine(_tempDir, "target.json");

        File.WriteAllText(sourceFile, JsonSerializer.Serialize(new
        {
            appConfig = new { Common = new { ApplicationName = "FromSource" } },
            OtherKey = "untouched"
        }));

        File.WriteAllText(targetFile, JsonSerializer.Serialize(new
        {
            appConfig = new { Common = new { ApplicationName = "OldValue" } },
            TargetKey = "preserved"
        }));

        ConfigurationHelper.PropagateAppConfigChanges(sourceFile, [targetFile]);

        var targetJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(targetFile));
        targetJson.GetProperty("appConfig").GetProperty("Common")
            .GetProperty("ApplicationName").GetString().Should().Be("FromSource");
        targetJson.GetProperty("TargetKey").GetString().Should().Be("preserved");
    }

    [Fact]
    public void PropagateAppConfigChanges_MultipleTargets_AllUpdated()
    {
        var sourceFile = Path.Combine(_tempDir, "source.json");
        var target1 = Path.Combine(_tempDir, "target1.json");
        var target2 = Path.Combine(_tempDir, "target2.json");

        File.WriteAllText(sourceFile, JsonSerializer.Serialize(new
        {
            appConfig = new { Version = "2.0" }
        }));
        File.WriteAllText(target1, JsonSerializer.Serialize(new { appConfig = new { Version = "1.0" } }));
        File.WriteAllText(target2, JsonSerializer.Serialize(new { appConfig = new { Version = "1.0" } }));

        ConfigurationHelper.PropagateAppConfigChanges(sourceFile, [target1, target2]);

        var t1 = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(target1));
        var t2 = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(target2));
        t1.GetProperty("appConfig").GetProperty("Version").GetString().Should().Be("2.0");
        t2.GetProperty("appConfig").GetProperty("Version").GetString().Should().Be("2.0");
    }

    [Fact]
    public void PropagateAppConfigChanges_MissingSourceFile_ThrowsFileNotFoundException()
    {
        var act = () => ConfigurationHelper.PropagateAppConfigChanges(
            Path.Combine(_tempDir, "nonexistent.json"), []);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void PropagateAppConfigChanges_NoAppConfigInSource_ThrowsInvalidOperationException()
    {
        var sourceFile = Path.Combine(_tempDir, "no-appconfig.json");
        File.WriteAllText(sourceFile, JsonSerializer.Serialize(new { SomeOtherKey = "value" }));

        var act = () => ConfigurationHelper.PropagateAppConfigChanges(sourceFile, []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*appConfig*");
    }

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
}
