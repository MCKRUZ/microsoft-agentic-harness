using Domain.AI.Identity;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="AgentIdentity"/> record — construction, defaults, value
/// semantics, and immutability via <c>with</c> expressions. Domain validation is
/// intentionally absent (validate at boundaries — see Application-layer validators).
/// </summary>
public sealed class AgentIdentityTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var identity = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity
        };

        identity.Id.Should().Be("agent-001");
        identity.Kind.Should().Be(AgentIdentityKind.ManagedIdentity);
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNull()
    {
        var identity = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.Development
        };

        identity.TenantId.Should().BeNull();
        identity.ObjectId.Should().BeNull();
        identity.Audience.Should().BeNull();
    }

    [Fact]
    public void FullyPopulated_AllFieldsSet()
    {
        var identity = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.FederatedCredential,
            TenantId = "tenant-contoso",
            ObjectId = "00000000-0000-0000-0000-000000000001",
            Audience = "api://my-agent"
        };

        identity.Id.Should().Be("agent-001");
        identity.Kind.Should().Be(AgentIdentityKind.FederatedCredential);
        identity.TenantId.Should().Be("tenant-contoso");
        identity.ObjectId.Should().Be("00000000-0000-0000-0000-000000000001");
        identity.Audience.Should().Be("api://my-agent");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var b = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentIds_AreNotEqual()
    {
        var a = new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.ManagedIdentity };
        var b = new AgentIdentity { Id = "agent-002", Kind = AgentIdentityKind.ManagedIdentity };

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equality_DifferentKinds_AreNotEqual()
    {
        var a = new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.ManagedIdentity };
        var b = new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.FederatedCredential };

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equality_DifferentTenants_AreNotEqual()
    {
        var a = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var b = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-b"
        };

        a.Should().NotBe(b);
    }

    [Fact]
    public void WithExpression_ChangesTenant_PreservesOtherFields()
    {
        var original = new AgentIdentity
        {
            Id = "agent-001",
            Kind = AgentIdentityKind.FederatedCredential,
            TenantId = "tenant-a",
            ObjectId = "oid-1",
            Audience = "api://x"
        };

        var modified = original with { TenantId = "tenant-b" };

        modified.Id.Should().Be("agent-001");
        modified.Kind.Should().Be(AgentIdentityKind.FederatedCredential);
        modified.TenantId.Should().Be("tenant-b");
        modified.ObjectId.Should().Be("oid-1");
        modified.Audience.Should().Be("api://x");
        original.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public void Development_Kind_AllowsMinimalIdentity()
    {
        // Development is the dev/test escape hatch — no tenant required, no Entra binding.
        var identity = new AgentIdentity
        {
            Id = "dev-agent",
            Kind = AgentIdentityKind.Development
        };

        identity.Kind.Should().Be(AgentIdentityKind.Development);
        identity.TenantId.Should().BeNull();
        identity.ObjectId.Should().BeNull();
    }
}
