using Application.AI.Common.Interfaces.Agent;
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
/// Tests for <see cref="KnowledgeScopeAccessor"/> — verifies the ambient (AsyncLocal) user/tenant
/// identity flows across DI-scope boundaries and background continuations, which is what keeps
/// memory isolation intact on the orchestrator / sub-plan / post-turn-write paths.
/// </summary>
public sealed class KnowledgeScopeAccessorTests
{
    private static KnowledgeScopeAccessor Create()
    {
        var agentContext = new Mock<IAgentExecutionContext>();
        var configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { DefaultTenantId = "cfg-tenant" }
                }
            }
        });
        return new KnowledgeScopeAccessor(agentContext.Object, configMonitor.Object);
    }

    [Fact]
    public void SetScope_IsObservedByAnotherInstance_SimulatingChildScope()
    {
        // The orchestrator / DAG planner run sub-agents in a fresh DI scope, which resolves a
        // DIFFERENT accessor instance. Identity must still flow to it via the ambient AsyncLocal.
        var entryScope = Create();
        var childScope = Create();

        entryScope.SetScope(userId: "user-a", tenantId: "tenant-1");

        childScope.UserId.Should().Be("user-a");
        childScope.TenantId.Should().Be("tenant-1");
    }

    [Fact]
    public async Task SetScope_FlowsIntoBackgroundContinuation()
    {
        // The conversation-to-knowledge write runs on a post-turn Task.Run after the request scope
        // is gone; the captured execution context must still carry the caller's identity.
        var accessor = Create();
        accessor.SetScope(userId: "user-a", tenantId: "tenant-1");

        string? observedUser = null;
        await Task.Run(() => observedUser = accessor.UserId);

        observedUser.Should().Be("user-a");
    }

    [Fact]
    public void Scope_FallsBackToConfigDefaultTenant_WhenUnset()
    {
        var accessor = Create();

        accessor.UserId.Should().BeNull();
        accessor.TenantId.Should().Be("cfg-tenant");
    }

    // --- Restore token (identity must not leak between units of work) ---

    [Fact]
    public void SetScope_DisposingToken_RestoresNullIdentity_WhenNothingWasSetBefore()
    {
        var accessor = Create();

        var token = accessor.SetScope(userId: "user-a", tenantId: "tenant-1");
        accessor.UserId.Should().Be("user-a");
        token.Dispose();

        accessor.UserId.Should().BeNull("the scope before the call was unset, so it must be unset again");
        accessor.TenantId.Should().Be("cfg-tenant", "tenant falls back to the config default once unscoped");
    }

    [Fact]
    public void SetScope_DisposingNestedToken_RestoresOuterIdentity_NotNull()
    {
        // The case a naive "clear on dispose" implementation gets wrong: an inner scope must hand the
        // OUTER caller's identity back, not blank the ambient scope.
        var accessor = Create();
        using var outer = accessor.SetScope(userId: "user-a", tenantId: "tenant-a");

        var inner = accessor.SetScope(userId: "user-b", tenantId: "tenant-b");
        accessor.UserId.Should().Be("user-b");
        inner.Dispose();

        accessor.UserId.Should().Be("user-a", "disposing the inner scope restores the outer caller");
        accessor.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public void SetScope_SequentialUnitsOfWork_DoNotLeakIdentityIntoTheNext()
    {
        // Reproduces the background-drain-loop hazard: a BackgroundService pulls job A, sets scope,
        // finishes, then pulls job B which carries no identity. Without the restore, job B runs as A.
        var accessor = Create();

        using (accessor.SetScope(userId: "user-a", tenantId: "tenant-a"))
        {
            accessor.UserId.Should().Be("user-a");
        }

        accessor.UserId.Should().BeNull("job A's identity must not still be ambient when job B starts");
    }

    [Fact]
    public void SetScope_TokenDisposedTwice_DoesNotClobberALaterScope()
    {
        // A double dispose must be inert. If it re-applied the captured "previous", it would silently
        // undo whatever scope has since been established.
        var accessor = Create();
        var token = accessor.SetScope(userId: "user-a", tenantId: "tenant-a");
        token.Dispose();

        using var later = accessor.SetScope(userId: "user-b", tenantId: "tenant-b");
        token.Dispose();

        accessor.UserId.Should().Be("user-b", "the stale token must not reinstate its captured previous value");
    }
}
