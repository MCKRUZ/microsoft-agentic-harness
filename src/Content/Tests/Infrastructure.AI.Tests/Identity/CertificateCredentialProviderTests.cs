using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="CertificateCredentialProvider"/> — required-field gating,
/// mutually-exclusive thumbprint/path config, file-not-found failure, and a happy
/// path using a self-signed cert PFX written to a temp file.
/// </summary>
public sealed class CertificateCredentialProviderTests : IDisposable
{
    private readonly string _certDir;
    private readonly List<string> _pathsToClean = [];

    public CertificateCredentialProviderTests()
    {
        _certDir = Path.Combine(Path.GetTempPath(), $"agent-identity-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_certDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in _pathsToClean)
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(_certDir)) Directory.Delete(_certDir, recursive: true);
        }
        catch { /* test teardown — best-effort */ }
    }

    private static IOptionsMonitor<AppConfig> Config(CertificateProviderConfig? cert = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    Certificate = cert ?? new CertificateProviderConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static CertificateCredentialProvider Build(CertificateProviderConfig? cert = null)
        => new(Config(cert), NullLogger<CertificateCredentialProvider>.Instance);

    private string CreateSelfSignedPfx(string password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=agent-identity-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.Combine(_certDir, $"test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        _pathsToClean.Add(path);
        return path;
    }

    [Fact]
    public void Kind_IsCertificate()
    {
        Build().Kind.Should().Be(AgentIdentityKind.Certificate);
    }

    [Theory]
    [InlineData(null, "tenant", "client")]
    [InlineData("agent", null, "client")]
    [InlineData("agent", "tenant", null)]
    public async Task ResolveAsync_MissingRequiredFields_ReturnsNotConfigured(
        string? agentId, string? tenantId, string? clientId)
    {
        var provider = Build(new CertificateProviderConfig
        {
            AgentId = agentId,
            TenantId = tenantId,
            ClientId = clientId,
            CertificateThumbprint = "AABBCC"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(CertificateCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_NeitherThumbprintNorPath_ReturnsNotConfigured()
    {
        var provider = Build(new CertificateProviderConfig
        {
            AgentId = "cert-agent",
            TenantId = "tenant-a",
            ClientId = "client-1"
            // CertificateThumbprint and CertificatePath both null
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(CertificateCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_BothThumbprintAndPath_ReturnsNotConfigured()
    {
        var provider = Build(new CertificateProviderConfig
        {
            AgentId = "cert-agent",
            TenantId = "tenant-a",
            ClientId = "client-1",
            CertificateThumbprint = "AABBCC",
            CertificatePath = "/some/path.pfx"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(CertificateCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_FileNotFound_ReturnsLoadFailedCode()
    {
        var provider = Build(new CertificateProviderConfig
        {
            AgentId = "cert-agent",
            TenantId = "tenant-a",
            ClientId = "client-1",
            CertificatePath = Path.Combine(_certDir, "does-not-exist.pfx")
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(CertificateCredentialProvider.CertificateLoadFailedCode);
    }

    [Fact]
    public async Task ResolveAsync_ValidPfxFile_ReturnsIdentity()
    {
        const string password = "test-pfx-password";
        var pfxPath = CreateSelfSignedPfx(password);

        var provider = Build(new CertificateProviderConfig
        {
            AgentId = "cert-agent",
            TenantId = "tenant-a",
            ClientId = "00000000-0000-0000-0000-000000000001",
            CertificatePath = pfxPath,
            CertificatePassword = password,
            ObjectId = "oid-1"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("cert-agent");
        result.Value.Kind.Should().Be(AgentIdentityKind.Certificate);
        result.Value.TenantId.Should().Be("tenant-a");
        result.Value.ObjectId.Should().Be("oid-1");
        result.Value.Audience.Should().Be("api://x");
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var provider = Build(new CertificateProviderConfig
        {
            AgentId = "cert-agent",
            TenantId = "tenant-a",
            ClientId = "client-1",
            CertificateThumbprint = "AABBCC"
        });

        var act = () => provider.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
