using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph;

/// <summary>
/// Tests for <see cref="KnowledgeScopeDescriptor"/> record — construction, defaults,
/// and multi-tenant scoping semantics.
/// </summary>
public sealed class KnowledgeScopeDescriptorTests
{
    [Fact]
    public void Constructor_WithRequiredProperties_SetsValues()
    {
        var scope = new KnowledgeScopeDescriptor { UserId = "user-1" };

        scope.UserId.Should().Be("user-1");
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNull()
    {
        var scope = new KnowledgeScopeDescriptor { UserId = "user-1" };

        scope.TenantId.Should().BeNull();
        scope.DatasetName.Should().BeNull();
        scope.DatasetId.Should().BeNull();
        scope.DatasetOwnerId.Should().BeNull();
    }

    [Fact]
    public void FullyPopulated_AllFieldsSet()
    {
        var scope = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-contoso",
            DatasetName = "research_papers",
            DatasetId = "ds-123",
            DatasetOwnerId = "user-admin"
        };

        scope.UserId.Should().Be("user-1");
        scope.TenantId.Should().Be("tenant-contoso");
        scope.DatasetName.Should().Be("research_papers");
        scope.DatasetId.Should().Be("ds-123");
        scope.DatasetOwnerId.Should().Be("user-admin");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var scope1 = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            DatasetId = "ds-1"
        };
        var scope2 = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-1",
            DatasetId = "ds-1"
        };

        scope1.Should().Be(scope2);
    }

    [Fact]
    public void Equality_DifferentTenants_AreNotEqual()
    {
        var scope1 = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-a"
        };
        var scope2 = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-b"
        };

        scope1.Should().NotBe(scope2);
    }

    [Fact]
    public void WithExpression_ChangesTenant_PreservesUser()
    {
        var original = new KnowledgeScopeDescriptor
        {
            UserId = "user-1",
            TenantId = "tenant-a",
            DatasetId = "ds-1"
        };

        var modified = original with { TenantId = "tenant-b" };

        modified.TenantId.Should().Be("tenant-b");
        modified.UserId.Should().Be("user-1");
        modified.DatasetId.Should().Be("ds-1");
        original.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public void SharedDataset_OwnerDiffersFromUser()
    {
        var scope = new KnowledgeScopeDescriptor
        {
            UserId = "user-reader",
            TenantId = "tenant-1",
            DatasetId = "ds-shared",
            DatasetOwnerId = "user-owner"
        };

        scope.UserId.Should().NotBe(scope.DatasetOwnerId);
        scope.DatasetOwnerId.Should().Be("user-owner");
    }
}
