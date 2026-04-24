using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Scoping;

/// <summary>
/// Tests for <see cref="KnowledgeScopeValidator"/> — tenant matching,
/// dataset ownership, and isolation toggle behavior.
/// </summary>
public sealed class KnowledgeScopeValidatorTests
{
    private readonly Mock<IOptionsMonitor<AppConfig>> _configMonitor;

    public KnowledgeScopeValidatorTests()
    {
        _configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
    }

    [Fact]
    public void ValidateAccess_IsolationDisabled_AlwaysAllows()
    {
        var validator = CreateValidator(isolationEnabled: false);
        var scope = CreateScope(tenantId: "t1");

        validator.ValidateAccess(scope, "different-tenant").Should().BeTrue();
    }

    [Fact]
    public void ValidateAccess_MatchingTenant_Allows()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(tenantId: "t1");

        validator.ValidateAccess(scope, "t1").Should().BeTrue();
    }

    [Fact]
    public void ValidateAccess_DifferentTenant_Denies()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(tenantId: "t1");

        validator.ValidateAccess(scope, "t2").Should().BeFalse();
    }

    [Fact]
    public void ValidateAccess_NullTargetTenant_Allows()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(tenantId: "t1");

        validator.ValidateAccess(scope, null).Should().BeTrue();
    }

    [Fact]
    public void ValidateAccess_NullScopeTenant_Denies()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(tenantId: null);

        validator.ValidateAccess(scope, "t1").Should().BeFalse();
    }

    [Fact]
    public void ValidateAccess_CaseInsensitive()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(tenantId: "Tenant-1");

        validator.ValidateAccess(scope, "tenant-1").Should().BeTrue();
    }

    [Fact]
    public void CanAccessDataset_IsolationDisabled_AlwaysAllows()
    {
        var validator = CreateValidator(isolationEnabled: false);
        var scope = CreateScope(userId: "u1");

        validator.CanAccessDataset(scope, "different-user").Should().BeTrue();
    }

    [Fact]
    public void CanAccessDataset_SameUser_Allows()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(userId: "u1");

        validator.CanAccessDataset(scope, "u1").Should().BeTrue();
    }

    [Fact]
    public void CanAccessDataset_DifferentUser_NullTenant_Denies()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(userId: "u1", tenantId: null);

        validator.CanAccessDataset(scope, "u2").Should().BeFalse();
    }

    private KnowledgeScopeValidator CreateValidator(bool isolationEnabled)
    {
        _configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { MultiTenantIsolation = isolationEnabled }
                }
            }
        });
        return new KnowledgeScopeValidator(_configMonitor.Object);
    }

    private static IKnowledgeScope CreateScope(
        string? userId = null,
        string? tenantId = null,
        string? datasetId = null)
    {
        var mock = new Mock<IKnowledgeScope>();
        mock.Setup(s => s.UserId).Returns(userId);
        mock.Setup(s => s.TenantId).Returns(tenantId);
        mock.Setup(s => s.DatasetId).Returns(datasetId);
        return mock.Object;
    }
}
