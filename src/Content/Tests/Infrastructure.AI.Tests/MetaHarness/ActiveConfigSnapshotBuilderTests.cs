using Application.AI.Common.Interfaces;
using Xunit;
using Application.AI.Common.Interfaces.MetaHarness;
using Domain.Common.Config.MetaHarness;
using Infrastructure.AI.MetaHarness;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for ActiveConfigSnapshotBuilder: secret exclusion, SHA256 hashing, and redaction.
/// </summary>
public class ActiveConfigSnapshotBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ISecretRedactor> _redactorMock;
    private readonly MetaHarnessConfig _config;

    public ActiveConfigSnapshotBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"harness-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _redactorMock = new Mock<ISecretRedactor>();
        _redactorMock
            .Setup(r => r.Redact(It.IsAny<string?>()))
            .Returns<string?>(s => s);

        _config = new MetaHarnessConfig
        {
            SnapshotConfigKeys = ["DatabaseName", "Region", "ApiKey"],
            SecretsRedactionPatterns = ["Key", "Secret", "Token", "Password", "ConnectionString"]
        };

        // Default IsSecretKey behaviour mirrors the config patterns (delegates to ISecretRedactor in production)
        _redactorMock
            .Setup(r => r.IsSecretKey(It.IsAny<string>()))
            .Returns<string>(key => _config.SecretsRedactionPatterns.Any(p =>
                key.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private ISnapshotBuilder BuildSut(MetaHarnessConfig? config = null)
    {
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(
            m => m.CurrentValue == (config ?? _config));
        return new ActiveConfigSnapshotBuilder(opts, _redactorMock.Object);
    }

    [Fact]
    public async Task Build_ExcludesSecretKeys_FromConfigSnapshot()
    {
        var sut = BuildSut();
        var configValues = new Dictionary<string, string>
        {
            ["ApiKey"] = "super-secret",
            ["DatabaseName"] = "mydb"
        };

        var snapshot = await sut.BuildAsync(_tempDir, "prompt", configValues);

        Assert.DoesNotContain("ApiKey", snapshot.ConfigSnapshot.Keys);
        Assert.Contains("DatabaseName", snapshot.ConfigSnapshot.Keys);
    }

    [Fact]
    public async Task Build_IncludesAllowlistedConfigKeys()
    {
        var sut = BuildSut();
        var configValues = new Dictionary<string, string>
        {
            ["DatabaseName"] = "mydb",
            ["Region"] = "eastus",
            ["Unrelated"] = "value"
        };

        var snapshot = await sut.BuildAsync(_tempDir, "prompt", configValues);

        Assert.Contains("DatabaseName", snapshot.ConfigSnapshot.Keys);
        Assert.Contains("Region", snapshot.ConfigSnapshot.Keys);
        Assert.DoesNotContain("Unrelated", snapshot.ConfigSnapshot.Keys);
    }

    [Fact]
    public async Task Build_ComputesSha256_ForEachSkillFile()
    {
        var file1 = Path.Combine(_tempDir, "SKILL.md");
        var file2 = Path.Combine(_tempDir, "TOOL.md");
        await File.WriteAllTextAsync(file1, "# Skill content");
        await File.WriteAllTextAsync(file2, "# Tool content");

        var sut = BuildSut();
        var snapshot = await sut.BuildAsync(_tempDir, "prompt", new Dictionary<string, string>());

        Assert.Equal(2, snapshot.SnapshotManifest.Count);
        Assert.All(snapshot.SnapshotManifest, entry =>
            Assert.False(string.IsNullOrEmpty(entry.Sha256Hash)));
    }

    [Fact]
    public async Task Build_AppliesRedactor_ToSystemPrompt()
    {
        _redactorMock
            .Setup(r => r.Redact("sensitive prompt"))
            .Returns("[REDACTED]");

        var sut = BuildSut();
        var snapshot = await sut.BuildAsync(_tempDir, "sensitive prompt", new Dictionary<string, string>());

        Assert.Equal("[REDACTED]", snapshot.SystemPromptSnapshot);
    }

    [Fact]
    public async Task Build_SnapshotManifest_ContainsCorrectHashes()
    {
        const string content = "Hello, harness!";
        var filePath = Path.Combine(_tempDir, "README.md");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        var sut = BuildSut();
        var snapshot = await sut.BuildAsync(_tempDir, "prompt", new Dictionary<string, string>());

        var entry = Assert.Single(snapshot.SnapshotManifest);
        Assert.Equal(expectedHash, entry.Sha256Hash);
    }
}
