using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Config;

/// <summary>
/// Tests for MCP configuration classes: <see cref="McpServerDefinition"/>,
/// <see cref="McpServerAuthConfig"/>, <see cref="McpConfig"/>,
/// and related enums.
/// </summary>
public class McpServerDefinitionTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var def = new McpServerDefinition();

        def.Enabled.Should().BeTrue();
        def.Type.Should().Be(McpServerType.Stdio);
        def.Command.Should().BeEmpty();
        def.Args.Should().BeEmpty();
        def.Env.Should().BeEmpty();
        def.Url.Should().BeNull();
        def.WorkingDirectory.Should().BeNull();
        def.StartupTimeoutSeconds.Should().Be(30);
        def.Description.Should().BeEmpty();
        def.Auth.Should().BeNull();
    }

    [Fact]
    public void RequiresAuth_WithNoAuth_ReturnsFalse()
    {
        var def = new McpServerDefinition();

        def.RequiresAuth.Should().BeFalse();
    }

    [Fact]
    public void RequiresAuth_WithAuthNone_ReturnsFalse()
    {
        var def = new McpServerDefinition
        {
            Auth = new McpServerAuthConfig { Type = McpServerAuthType.None }
        };

        def.RequiresAuth.Should().BeFalse();
    }

    [Fact]
    public void RequiresAuth_WithAuthBearer_ReturnsTrue()
    {
        var def = new McpServerDefinition
        {
            Auth = new McpServerAuthConfig { Type = McpServerAuthType.Bearer }
        };

        def.RequiresAuth.Should().BeTrue();
    }

    [Theory]
    [InlineData(McpServerType.Http, true)]
    [InlineData(McpServerType.Sse, true)]
    [InlineData(McpServerType.Stdio, false)]
    public void IsRemoteServer_ReturnsExpected(McpServerType type, bool expected)
    {
        var def = new McpServerDefinition { Type = type };

        def.IsRemoteServer.Should().Be(expected);
    }
}

/// <summary>
/// Tests for <see cref="McpServerAuthConfig"/> validation and defaults.
/// </summary>
public class McpServerAuthConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var auth = new McpServerAuthConfig();

        auth.Type.Should().Be(McpServerAuthType.None);
        auth.ApiKey.Should().BeNull();
        auth.ApiKeyHeader.Should().Be("X-API-Key");
        auth.BearerToken.Should().BeNull();
        auth.TenantId.Should().BeNull();
        auth.ClientId.Should().BeNull();
        auth.ClientSecret.Should().BeNull();
        auth.CertificatePath.Should().BeNull();
        auth.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void IsConfigured_WithNone_ReturnsFalse()
    {
        var auth = new McpServerAuthConfig { Type = McpServerAuthType.None };

        auth.IsConfigured.Should().BeFalse();
    }

    [Theory]
    [InlineData(McpServerAuthType.ApiKey)]
    [InlineData(McpServerAuthType.Bearer)]
    [InlineData(McpServerAuthType.Entra)]
    public void IsConfigured_WithNonNone_ReturnsTrue(McpServerAuthType type)
    {
        var auth = new McpServerAuthConfig { Type = type };

        auth.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsValid_None_ReturnsTrue()
    {
        var auth = new McpServerAuthConfig { Type = McpServerAuthType.None };

        auth.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_ApiKey_WithKey_ReturnsTrue()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.ApiKey,
            ApiKey = "my-key"
        };

        auth.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_ApiKey_WithoutKey_ReturnsFalse()
    {
        var auth = new McpServerAuthConfig { Type = McpServerAuthType.ApiKey };

        auth.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_Bearer_WithToken_ReturnsTrue()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Bearer,
            BearerToken = "token-123"
        };

        auth.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_Bearer_WithoutToken_ReturnsFalse()
    {
        var auth = new McpServerAuthConfig { Type = McpServerAuthType.Bearer };

        auth.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_Entra_WithClientSecret_ReturnsTrue()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = "tenant-1",
            ClientId = "client-1",
            ClientSecret = "secret-1"
        };

        auth.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_Entra_WithCertificate_ReturnsTrue()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = "tenant-1",
            ClientId = "client-1",
            CertificatePath = "/certs/client.pfx"
        };

        auth.IsValid.Should().BeTrue();
    }

    [Fact]
    public void IsValid_Entra_MissingTenantId_ReturnsFalse()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            ClientId = "client-1",
            ClientSecret = "secret-1"
        };

        auth.IsValid.Should().BeFalse();
    }

    [Fact]
    public void IsValid_Entra_MissingBothSecretAndCert_ReturnsFalse()
    {
        var auth = new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = "tenant-1",
            ClientId = "client-1"
        };

        auth.IsValid.Should().BeFalse();
    }
}

/// <summary>
/// Tests for MCP-related enums.
/// </summary>
public class McpEnumTests
{
    [Theory]
    [InlineData(McpServerType.Stdio, 0)]
    [InlineData(McpServerType.Sse, 1)]
    [InlineData(McpServerType.Http, 2)]
    public void McpServerType_HasExpectedValues(McpServerType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Theory]
    [InlineData(McpServerAuthType.None, 0)]
    [InlineData(McpServerAuthType.ApiKey, 1)]
    [InlineData(McpServerAuthType.Bearer, 2)]
    [InlineData(McpServerAuthType.Entra, 3)]
    public void McpServerAuthType_HasExpectedValues(McpServerAuthType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }
}

/// <summary>
/// Tests for <see cref="McpConfig"/> server-side configuration defaults.
/// </summary>
public class McpServerConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new McpConfig();

        config.ServerName.Should().Be("agentic-harness");
        config.ServerVersion.Should().Be("1.0.0");
        config.ServerInstructions.Should().NotBeNullOrEmpty();
        config.InitializationTimeout.Should().Be(TimeSpan.FromSeconds(60));
        config.Auth.Should().NotBeNull();
        config.ScanAssemblies.Should().BeEmpty();
    }
}

/// <summary>
/// Tests for <see cref="McpServersConfig"/> client-side configuration.
/// </summary>
public class McpServersConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new McpServersConfig();

        config.Servers.Should().BeEmpty();
    }

    [Fact]
    public void Servers_CanBePopulated()
    {
        var config = new McpServersConfig();
        config.Servers["filesystem"] = new McpServerDefinition
        {
            Type = McpServerType.Stdio,
            Command = "npx",
            Description = "File system access"
        };

        config.Servers.Should().ContainKey("filesystem");
        config.Servers["filesystem"].Command.Should().Be("npx");
    }
}
