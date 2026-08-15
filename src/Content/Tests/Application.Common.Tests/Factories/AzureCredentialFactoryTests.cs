using Application.Common.Factories;
using Domain.Common.Config.Azure;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Factories;

public class AzureCredentialFactoryTests
{
    [Fact]
    public void BuildDefaultAzureCredentialOptions_ExcludeManagedIdentityCredentialNotSet_DefaultsToFalse()
    {
        var config = new EntraCredentialConfig();

        var options = AzureCredentialFactory.BuildDefaultAzureCredentialOptions(config);

        options.ExcludeManagedIdentityCredential.Should().BeFalse(
            "omitting the VDI workaround must preserve today's default DefaultAzureCredential behavior");
    }

    [Fact]
    public void BuildDefaultAzureCredentialOptions_ExcludeManagedIdentityCredentialSet_ThreadsThrough()
    {
        var config = new EntraCredentialConfig { ExcludeManagedIdentityCredential = true };

        var options = AzureCredentialFactory.BuildDefaultAzureCredentialOptions(config);

        options.ExcludeManagedIdentityCredential.Should().BeTrue();
    }

    [Fact]
    public void BuildDefaultAzureCredentialOptions_TenantIdAndClientIdSet_MapsToChainOptions()
    {
        var config = new EntraCredentialConfig
        {
            TenantId = "tenant-123",
            ClientId = "client-456"
        };

        var options = AzureCredentialFactory.BuildDefaultAzureCredentialOptions(config);

        options.TenantId.Should().Be("tenant-123");
        options.ManagedIdentityClientId.Should().Be("client-456");
    }

    [Fact]
    public void CreateTokenCredential_ClientSecretConfigured_ReturnsClientSecretCredential()
    {
        var config = new EntraCredentialConfig
        {
            TenantId = "tenant-123",
            ClientId = "client-456",
            ClientSecret = "secret-789"
        };

        var credential = AzureCredentialFactory.CreateTokenCredential(config);

        credential.Should().BeOfType<Azure.Identity.ClientSecretCredential>();
    }

    [Fact]
    public void CreateTokenCredential_NoExplicitCredentialsConfigured_ReturnsDefaultAzureCredential()
    {
        var config = new EntraCredentialConfig();

        var credential = AzureCredentialFactory.CreateTokenCredential(config);

        credential.Should().BeOfType<Azure.Identity.DefaultAzureCredential>();
    }

    [Fact]
    public void CreateTokenCredential_NullConfig_Throws()
    {
        var act = () => AzureCredentialFactory.CreateTokenCredential(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
