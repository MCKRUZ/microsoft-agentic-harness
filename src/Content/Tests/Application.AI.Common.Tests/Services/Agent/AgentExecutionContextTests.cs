using Application.AI.Common.Services.Agent;
using Domain.AI.Identity;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="AgentExecutionContext"/> covering initialization,
/// re-initialization rules, and scope conflict detection.
/// </summary>
public class AgentExecutionContextTests
{
    [Fact]
    public void NewContext_AllPropertiesAreNull()
    {
        var context = new AgentExecutionContext();

        context.AgentId.Should().BeNull();
        context.ConversationId.Should().BeNull();
        context.TurnNumber.Should().BeNull();
    }

    [Fact]
    public void Initialize_SetsAllProperties()
    {
        var context = new AgentExecutionContext();

        context.Initialize("planner", "conv-1", 1);

        context.AgentId.Should().Be("planner");
        context.ConversationId.Should().Be("conv-1");
        context.TurnNumber.Should().Be(1);
    }

    [Fact]
    public void Initialize_SameAgentAndConversation_UpdatesTurnNumber()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        context.Initialize("planner", "conv-1", 2);

        context.TurnNumber.Should().Be(2);
    }

    [Fact]
    public void Initialize_DifferentAgent_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("reviewer", "conv-1", 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scope conflict*")
            .WithMessage("*planner*")
            .WithMessage("*reviewer*");
    }

    [Fact]
    public void Initialize_DifferentConversation_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("planner", "conv-2", 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scope conflict*")
            .WithMessage("*conv-1*")
            .WithMessage("*conv-2*");
    }

    [Fact]
    public void Initialize_DifferentAgentAndConversation_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("reviewer", "conv-2", 1);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Initialize_MultipleTurns_TracksLatestTurn()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);
        context.Initialize("planner", "conv-1", 2);
        context.Initialize("planner", "conv-1", 5);

        context.TurnNumber.Should().Be(5);
    }

    // --- Agent identity (PR-1 step 3) -----------------------------------------

    [Fact]
    public void NewContext_AgentIdentity_IsNull()
    {
        var context = new AgentExecutionContext();

        context.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public void SetIdentity_StoresIdentity()
    {
        var context = new AgentExecutionContext();
        var identity = new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity
        };

        context.SetIdentity(identity);

        context.AgentIdentity.Should().Be(identity);
    }

    [Fact]
    public void SetIdentity_NullIdentity_ThrowsArgumentNull()
    {
        var context = new AgentExecutionContext();

        var act = () => context.SetIdentity(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetIdentity_SameValueTwice_IsIdempotent()
    {
        var context = new AgentExecutionContext();
        var first = new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var sameValue = new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };

        context.SetIdentity(first);
        var act = () => context.SetIdentity(sameValue);

        act.Should().NotThrow();
        context.AgentIdentity.Should().Be(first);
    }

    [Fact]
    public void SetIdentity_DifferentId_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity });

        var act = () => context.SetIdentity(new AgentIdentity { Id = "reviewer", Kind = AgentIdentityKind.ManagedIdentity });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*identity conflict*")
            .WithMessage("*planner*")
            .WithMessage("*reviewer*");
    }

    [Fact]
    public void SetIdentity_DifferentKind_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity });

        var act = () => context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.FederatedCredential });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*identity conflict*");
    }

    [Fact]
    public void SetIdentity_DifferentTenant_ThrowsInvalidOperation()
    {
        var context = new AgentExecutionContext();
        context.SetIdentity(new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        });

        var act = () => context.SetIdentity(new AgentIdentity
        {
            Id = "planner",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-b"
        });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetIdentity_DoesNotAffectAgentOrConversation()
    {
        var context = new AgentExecutionContext();
        context.Initialize("planner", "conv-1", 1);

        context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity });

        context.AgentId.Should().Be("planner");
        context.ConversationId.Should().Be("conv-1");
        context.TurnNumber.Should().Be(1);
    }

    [Fact]
    public void Initialize_AfterSetIdentity_PreservesIdentity()
    {
        var context = new AgentExecutionContext();
        var identity = new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity };

        context.SetIdentity(identity);
        context.Initialize("planner", "conv-1", 1);
        context.Initialize("planner", "conv-1", 2);

        context.AgentIdentity.Should().Be(identity);
    }

    // --- Thread-safety contract (interface xmldoc: "implementation must be thread-safe") ---

    [Fact]
    public void Initialize_Concurrent_DifferentAgents_ExactlyOneSucceeds()
    {
        // Without locking, check-then-set on _initialized is a TOCTOU race where multiple
        // threads can observe _initialized = false, all pass the conflict check, and last
        // writer wins silently. With the lock, one thread succeeds; the rest throw.
        const int threadCount = 32;
        var context = new AgentExecutionContext();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception?>();
        var ready = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
        {
            ready.Wait();
            try
            {
                context.Initialize($"agent-{i}", $"conv-{i}", 1);
                exceptions.Add(null);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        ready.Set();
        foreach (var t in threads) t.Join();

        exceptions.Should().HaveCount(threadCount);
        exceptions.Count(e => e is null).Should().Be(1, "exactly one thread should win the race");
        exceptions.Where(e => e is not null).Should().AllBeOfType<InvalidOperationException>();
        context.AgentId.Should().NotBeNull();
    }

    [Fact]
    public void SetIdentity_Concurrent_DifferentIdentities_ExactlyOneSucceeds()
    {
        const int threadCount = 32;
        var context = new AgentExecutionContext();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception?>();
        var ready = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
        {
            ready.Wait();
            var identity = new AgentIdentity
            {
                Id = $"agent-{i}",
                Kind = AgentIdentityKind.ManagedIdentity
            };
            try
            {
                context.SetIdentity(identity);
                exceptions.Add(null);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        ready.Set();
        foreach (var t in threads) t.Join();

        exceptions.Should().HaveCount(threadCount);
        exceptions.Count(e => e is null).Should().Be(1, "exactly one thread should win the race");
        exceptions.Where(e => e is not null).Should().AllBeOfType<InvalidOperationException>();
        context.AgentIdentity.Should().NotBeNull();
    }

    [Fact]
    public void SetIdentity_Concurrent_SameIdentity_AllThreadsSucceed()
    {
        // Idempotent re-set with a value-equal identity must not throw under contention.
        // The early-return path inside the lock is the contract; this proves it.
        const int threadCount = 32;
        var context = new AgentExecutionContext();
        var identityTemplate = new AgentIdentity
        {
            Id = "shared-agent",
            Kind = AgentIdentityKind.ManagedIdentity,
            TenantId = "tenant-a"
        };
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception?>();
        var ready = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            ready.Wait();
            // New record instance per thread with identical values — exercises value
            // equality, not reference equality.
            var identity = identityTemplate with { };
            try
            {
                context.SetIdentity(identity);
                exceptions.Add(null);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        ready.Set();
        foreach (var t in threads) t.Join();

        exceptions.Should().HaveCount(threadCount);
        exceptions.Should().AllSatisfy(e => e.Should().BeNull());
        context.AgentIdentity.Should().Be(identityTemplate);
    }
}
