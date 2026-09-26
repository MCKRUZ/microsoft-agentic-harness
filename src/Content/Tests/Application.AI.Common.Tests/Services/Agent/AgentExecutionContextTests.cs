using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Telemetry;
using Domain.AI.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests for <see cref="AgentExecutionContext"/> covering initialization,
/// re-initialization rules, scope conflict detection, and the external governance attribution it
/// publishes for the turn.
/// </summary>
public class AgentExecutionContextTests
{
    /// <summary>
    /// A context wired to the benign no-op attribution — the shape every host that has not opted into
    /// an agent-governance integration gets. Used wherever a test is about the context's own state
    /// rather than about attribution.
    /// </summary>
    private static AgentExecutionContext NewContext()
        => new(new NoOpAgentTelemetryAttribution());

    [Fact]
    public void NewContext_AllPropertiesAreNull()
    {
        var context = NewContext();

        context.AgentId.Should().BeNull();
        context.ConversationId.Should().BeNull();
        context.TurnNumber.Should().BeNull();
    }

    [Fact]
    public void Initialize_SetsAllProperties()
    {
        var context = NewContext();

        context.Initialize("planner", "conv-1", 1);

        context.AgentId.Should().Be("planner");
        context.ConversationId.Should().Be("conv-1");
        context.TurnNumber.Should().Be(1);
    }

    [Fact]
    public void Initialize_SameAgentAndConversation_UpdatesTurnNumber()
    {
        var context = NewContext();
        context.Initialize("planner", "conv-1", 1);

        context.Initialize("planner", "conv-1", 2);

        context.TurnNumber.Should().Be(2);
    }

    [Fact]
    public void Initialize_DifferentAgent_ThrowsInvalidOperation()
    {
        var context = NewContext();
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
        var context = NewContext();
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
        var context = NewContext();
        context.Initialize("planner", "conv-1", 1);

        var act = () => context.Initialize("reviewer", "conv-2", 1);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Initialize_MultipleTurns_TracksLatestTurn()
    {
        var context = NewContext();
        context.Initialize("planner", "conv-1", 1);
        context.Initialize("planner", "conv-1", 2);
        context.Initialize("planner", "conv-1", 5);

        context.TurnNumber.Should().Be(5);
    }

    // --- Agent identity (PR-1 step 3) -----------------------------------------

    [Fact]
    public void NewContext_AgentIdentity_IsNull()
    {
        var context = NewContext();

        context.AgentIdentity.Should().BeNull();
    }

    [Fact]
    public void SetIdentity_StoresIdentity()
    {
        var context = NewContext();
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
        var context = NewContext();

        var act = () => context.SetIdentity(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetIdentity_SameValueTwice_IsIdempotent()
    {
        var context = NewContext();
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
        var context = NewContext();
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
        var context = NewContext();
        context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity });

        var act = () => context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.FederatedCredential });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*identity conflict*");
    }

    [Fact]
    public void SetIdentity_DifferentTenant_ThrowsInvalidOperation()
    {
        var context = NewContext();
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
        var context = NewContext();
        context.Initialize("planner", "conv-1", 1);

        context.SetIdentity(new AgentIdentity { Id = "planner", Kind = AgentIdentityKind.ManagedIdentity });

        context.AgentId.Should().Be("planner");
        context.ConversationId.Should().Be("conv-1");
        context.TurnNumber.Should().Be(1);
    }

    [Fact]
    public void Initialize_AfterSetIdentity_PreservesIdentity()
    {
        var context = NewContext();
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
        var context = NewContext();
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
        var context = NewContext();
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
        var context = NewContext();
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

    // --- ToolResultScopeId freeze-on-first-read (regression: GitHub #562) ---------
    // CallOnceScopeId ?? _fallbackToolResultScopeId flips the moment Initialize supplies a
    // non-null call-once scope. A reader that observes the fallback before Initialize runs,
    // then reads again after, must not see the value change underneath it — a result spilled
    // under the first value would become permanently unfindable under the second.

    [Fact]
    public void ToolResultScopeId_ReadTwiceWithNoInitializeBetween_ReturnsTheSameValue()
    {
        var context = NewContext();

        var first = context.ToolResultScopeId;
        var second = context.ToolResultScopeId;

        second.Should().Be(first);
    }

    [Fact]
    public void ToolResultScopeId_ReadBeforeInitialize_ThenInitializeWithNoCallOnceScope_ReturnsTheSameValue()
    {
        // The fallback GUID is what ToolResultScopeId already resolved to; Initialize with no
        // call-once scope reproduces exactly that value, so this is the one realistic case
        // where reading early does not conflict with what Initialize later supplies.
        var context = NewContext();

        var beforeInitialize = context.ToolResultScopeId;
        context.Initialize("planner", "conv-1", 1);
        var afterInitialize = context.ToolResultScopeId;

        afterInitialize.Should().Be(beforeInitialize);
    }

    [Fact]
    public void Initialize_AfterScopeIdWasObserved_WithADifferentCallOnceScopeId_Throws()
    {
        // Reading before Initialize observes the fallback GUID; supplying a real call-once
        // scope afterward would silently change what ToolResultScopeId means to that reader,
        // orphaning anything already spilled under the fallback. Must fail loudly instead.
        var context = NewContext();
        _ = context.ToolResultScopeId;

        var act = () => context.Initialize("planner", "conv-1", 1, callOnceScopeId: "conv-1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ToolResultScopeId was already read*");
    }

    // --- Per-run DI scoping contract (regression: GitHub #19) ---------------------
    // ResearchAgentExample reused a root-scoped ISender across runs, so the scoped
    // IAgentExecutionContext was bound on the first turn and collided on the second
    // ("AgentExecutionContext scope conflict"), forcing an app restart. The fix runs
    // each turn in a fresh DI scope. These two tests pin both halves of that contract.

    [Fact]
    public void ScopedContext_ReusedWithinOneScope_ThrowsScopeConflictOnSecondTurn()
    {
        // Reproduces the bug: a single scope hands back the same scoped instance, so the
        // second turn's Initialize collides with the first turn's binding.
        using var provider = new ServiceCollection()
            .AddSingleton<IAgentTelemetryAttribution, NoOpAgentTelemetryAttribution>()
            .AddScoped<IAgentExecutionContext, AgentExecutionContext>()
            .BuildServiceProvider();

        var firstTurn = provider.GetRequiredService<IAgentExecutionContext>();
        firstTurn.Initialize("research-agent", "conv-1", 1);

        var reused = provider.GetRequiredService<IAgentExecutionContext>();
        var act = () => reused.Initialize("research-agent", "conv-2", 1);

        reused.Should().BeSameAs(firstTurn, "a single scope resolves the scoped context once");
        act.Should().Throw<InvalidOperationException>().WithMessage("*scope conflict*");
    }

    [Fact]
    public void ScopedContext_FreshScopePerTurn_NoConflict()
    {
        // Proves the fix: a new scope per turn yields a fresh, unbound context each time.
        using var provider = new ServiceCollection()
            .AddSingleton<IAgentTelemetryAttribution, NoOpAgentTelemetryAttribution>()
            .AddScoped<IAgentExecutionContext, AgentExecutionContext>()
            .BuildServiceProvider();

        using (var firstScope = provider.CreateScope())
        {
            firstScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("research-agent", "conv-1", 1);
        }

        using var secondScope = provider.CreateScope();
        var secondTurn = secondScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        var act = () => secondTurn.Initialize("research-agent", "conv-2", 1);

        act.Should().NotThrow();
        secondTurn.ConversationId.Should().Be("conv-2");
    }

    // --- External governance attribution (#737) -----------------------------------
    //
    // Attribution used to be a second call each site made next to Initialize, and three of the five
    // sites that establish an execution context never made it — so plan runs, sub-plans and direct
    // tool invocations exported spans with no agent identity, which a governance platform discards
    // WITHOUT reporting an error. These tests pin the property that replaced the convention: you
    // cannot initialize a context without publishing attribution, and you cannot publish it without
    // something releasing it.

    [Fact]
    public void Initialize_PublishesAttributionForTheTurn()
    {
        var attribution = new RecordingAttribution();
        using var context = new AgentExecutionContext(attribution);

        context.Initialize("planner", "conv-1", 1);

        // Asserted by value, not merely by count: a governance platform matches the published agent
        // against the authenticated caller, so publishing the wrong id fails as surely as publishing
        // none. This is the assertion that fails if the publish is deleted from Initialize.
        attribution.Turns.Should().ContainSingle()
            .Which.Should().Be(("planner", "conv-1"));
    }

    [Fact]
    public void NewContext_NeverInitialized_PublishesNothing()
    {
        // A scope resolved for a non-agent request must not attribute anything to an agent.
        var attribution = new RecordingAttribution();
        using var context = new AgentExecutionContext(attribution);

        attribution.Turns.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_ReleasesTheAttribution()
    {
        // This is what makes the binding safe at every call site rather than only the ones that
        // remembered a using. The container disposes the scoped context when the scope ends, so
        // release needs no cooperation from the caller.
        var attribution = new RecordingAttribution();
        var context = new AgentExecutionContext(attribution);
        context.Initialize("planner", "conv-1", 1);

        attribution.Released.Should().Be(0, "the turn is still running");

        context.Dispose();

        attribution.Released.Should().Be(1);
    }

    [Fact]
    public void Dispose_CalledTwice_ReleasesTheAttributionOnce()
    {
        // A caller may dispose this directly AND let the container dispose it. Releasing twice would
        // restore the enclosing turn's baggage a second time, unpublishing attribution that a still
        // running outer turn depends on. Dispose takes the scope and clears the field in one locked
        // step, so the second call finds nothing to release — this pins that, since the obvious
        // alternative (an early-return flag) reads as the mechanism and is not one.
        var attribution = new RecordingAttribution();
        var context = new AgentExecutionContext(attribution);
        context.Initialize("planner", "conv-1", 1);

        context.Dispose();
        context.Dispose();

        attribution.Released.Should().Be(1);
    }

    [Fact]
    public void ReInitializeForALaterTurn_DoesNotPublishASecondTime()
    {
        // Re-initialization is allowed within one scope to bump the turn number, and the scope-leak
        // guard rejects any change to agent or conversation — the only two values attribution carries.
        // So a second publish could only replace the live scope with an identical one while leaking the
        // first, which never gets released.
        var attribution = new RecordingAttribution();
        using var context = new AgentExecutionContext(attribution);

        context.Initialize("planner", "conv-1", 1);
        context.Initialize("planner", "conv-1", 2);

        attribution.Turns.Should().HaveCount(1);
    }

    [Fact]
    public void Initialize_AfterDispose_PublishesNothing()
    {
        // Nothing reaches a disposed scoped service today, but publishing here would create a scope
        // with nothing left to release it — the one leak this design could otherwise introduce.
        var attribution = new RecordingAttribution();
        var context = new AgentExecutionContext(attribution);
        context.Dispose();

        context.Initialize("planner", "conv-1", 1);

        attribution.Turns.Should().BeEmpty();
        attribution.Released.Should().Be(0);
    }

    [Fact]
    public void ScopeDisposal_ReleasesTheAttribution_WithoutTheCallerDoingAnything()
    {
        // The whole point, proven through the real container rather than a direct Dispose call: a call
        // site that only ever calls Initialize still gets its attribution released, because the context
        // is registered scoped and the container owns its lifetime. Every path that initializes a
        // context runs inside a scope that is disposed — the four non-MediatR sites create one
        // explicitly, the MediatR path runs in the request scope the host disposes.
        var attribution = new RecordingAttribution();
        using var provider = new ServiceCollection()
            .AddSingleton<IAgentTelemetryAttribution>(attribution)
            .AddScoped<IAgentExecutionContext, AgentExecutionContext>()
            .BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
                .Initialize("planner", "conv-1", 1);

            attribution.Turns.Should().ContainSingle();
            attribution.Released.Should().Be(0);
        }

        attribution.Released.Should().Be(1, "the container disposes the scoped context with the scope");
    }

    /// <summary>
    /// Records what was published and how many scopes were released, so a test can tell "published
    /// nothing" apart from "published and immediately released".
    /// </summary>
    private sealed class RecordingAttribution : IAgentTelemetryAttribution
    {
        private readonly List<(string AgentId, string ConversationId)> _turns = [];

        public IReadOnlyList<(string AgentId, string ConversationId)> Turns => _turns;

        public int Released { get; private set; }

        public IDisposable BeginTurn(string agentId, string conversationId)
        {
            _turns.Add((agentId, conversationId));
            return new Release(this);
        }

        private sealed class Release : IDisposable
        {
            private readonly RecordingAttribution _owner;

            public Release(RecordingAttribution owner) => _owner = owner;

            public void Dispose() => _owner.Released++;
        }
    }
}
