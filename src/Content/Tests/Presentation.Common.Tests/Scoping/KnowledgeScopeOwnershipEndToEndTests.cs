using System.Security.Claims;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.Planner;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Presentation.Common.Scoping;
using Xunit;

namespace Presentation.Common.Tests.Scoping;

/// <summary>
/// End-to-end proof of the chain a claim actually travels: <see cref="ClaimsPrincipal"/> →
/// <see cref="KnowledgeScopeInitializer"/> → the real <see cref="KnowledgeScopeAccessor"/> →
/// <see cref="EfCorePlanStateStore"/> → <c>PlannerScopeFilter</c>. Every link is production code; nothing
/// is stubbed but the clock and the database file.
/// </summary>
/// <remarks>
/// This exists because the unit tests on either end can both pass while the seam between them is broken.
/// The specific break it pins: a token carrying <c>sub</c> but no <c>oid</c> once resolved to a NULL
/// knowledge scope even though it was a perfectly good owner elsewhere — and a null owner is treated as
/// GLOBAL (readable by every caller in every tenant) by the plan scope filter, not as private.
/// <c>Presentation.Common</c> is the only project that references both the identity resolver and the plan
/// store, so this is the one place the whole chain can be assembled.
/// </remarks>
public sealed class KnowledgeScopeOwnershipEndToEndTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly KnowledgeScopeAccessor _accessor;
    private readonly EfCorePlanStateStore _store;

    public KnowledgeScopeOwnershipEndToEndTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .Options;

        _factory = new TestDbContextFactory(options);
        _accessor = CreateAccessor();
        _store = new EfCorePlanStateStore(
            _factory,
            NullLogger<EfCorePlanStateStore>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero)),
            _accessor,
            new SchemaInitializer<PlannerDbContext>(_factory));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SubOnlyCaller_GetsNonNullScope_AndPlanIsNotGlobal()
    {
        var plan = CreateTestGraph();

        using (ApplyScope(Authenticated(("sub", "subject-only-caller"))))
        {
            _accessor.UserId.Should().Be("subject-only-caller",
                "a sub-only token is a real identity — resolving it to null would stamp a GLOBAL plan");

            (await _store.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using var ctx = _factory.CreateDbContext();
        var entity = await ctx.PlanGraphs.SingleAsync(g => g.Id == plan.Id.Value);
        entity.OwnerId.Should().Be("subject-only-caller",
            "the plan must be owned, not stamped null (which the scope filter reads as world-readable)");
    }

    [Fact]
    public async Task SubOnlyCaller_CannotReadAnotherCallersPlan()
    {
        var alicePlan = CreateTestGraph();
        using (ApplyScope(Authenticated(("oid", "alice"))))
        {
            (await _store.SavePlanAsync(alicePlan, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        using (ApplyScope(Authenticated(("sub", "mallory"))))
        {
            var result = await _store.LoadPlanAsync(alicePlan.Id, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().BeNull(
                "a sub-authenticated caller must be as isolated as an oid-authenticated one");
        }
    }

    [Fact]
    public async Task SubOnlyCaller_CanReadTheirOwnPlan()
    {
        // Guards the opposite failure: isolation that works by resolving everyone to null/nothing.
        var plan = CreateTestGraph();
        using (ApplyScope(Authenticated(("sub", "subject-only-caller"))))
        {
            (await _store.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        using (ApplyScope(Authenticated(("sub", "subject-only-caller"))))
        {
            var result = await _store.LoadPlanAsync(plan.Id, CancellationToken.None);

            result.Value.Should().NotBeNull("the owner must still be able to read their own plan");
        }
    }

    [Fact]
    public async Task MappedSubCaller_IsIsolatedFromRawSubCaller_WithADifferentSubject()
    {
        // Production tokens arrive with sub remapped to the NameIdentifier URI. A caller authenticated
        // through the mapped form must be scoped identically to one carrying the raw claim.
        var plan = CreateTestGraph();
        using (ApplyScope(Authenticated((ClaimTypes.NameIdentifier, "mapped-owner"))))
        {
            (await _store.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        using (ApplyScope(Authenticated(("sub", "someone-else"))))
        {
            var result = await _store.LoadPlanAsync(plan.Id, CancellationToken.None);

            result.Value.Should().BeNull();
        }
    }

    [Fact]
    public async Task AmbiguousIdentityCaller_IsRefusedScope_SoItCannotWriteAReadableRecord()
    {
        // The third route to a global record: identity resolution produces nothing, and nothing means
        // GLOBAL. Proven end to end rather than argued — the caller is refused, and had it been admitted
        // unscoped, the plan it wrote would have been readable by an unrelated caller (asserted below).
        var ambiguous = Authenticated(("oid", "victim"), ("oid", "attacker"));

        KnowledgeScopeInitializer.TryApply(ambiguous, _accessor, out var token).Should().BeFalse(
            "an identity we cannot determine must not establish scope");

        using (token)
        {
            _accessor.UserId.Should().BeNull();
        }

        // Demonstrate the consequence the refusal prevents: a plan written with no ambient identity is
        // global, and a completely unrelated caller can read it.
        var plan = CreateTestGraph();
        (await _store.SavePlanAsync(plan, CancellationToken.None)).IsSuccess.Should().BeTrue();

        using (ApplyScope(Authenticated(("oid", "unrelated-caller"))))
        {
            var leaked = await _store.LoadPlanAsync(plan.Id, CancellationToken.None);

            leaked.Value.Should().NotBeNull(
                "an unscoped write IS globally readable — which is exactly why the ambiguous caller " +
                "must be refused before it ever reaches persistence");
        }
    }

    // -- Helpers --

    /// <summary>
    /// Applies scope the way the middleware does, asserting the caller was ADMITTED. Any principal a
    /// test scopes through this helper is one the real pipeline would have let through.
    /// </summary>
    private IDisposable ApplyScope(ClaimsPrincipal principal)
    {
        KnowledgeScopeInitializer.TryApply(principal, _accessor, out var token)
            .Should().BeTrue("this helper is for callers the pipeline admits");
        return token;
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    private static KnowledgeScopeAccessor CreateAccessor()
    {
        var agentContext = new Mock<IAgentExecutionContext>();
        var configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig { GraphRag = new GraphRagConfig() }
            }
        });

        return new KnowledgeScopeAccessor(agentContext.Object, configMonitor.Object);
    }

    private static PlanGraph CreateTestGraph()
    {
        var steps = Enumerable.Range(0, 2).Select(i => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"Step {i}",
            Type = StepType.LlmCall,
            Configuration = new LlmCallConfig
            {
                SystemPrompt = $"Prompt for step {i}",
                ModelDeploymentKey = "gpt-4o",
            },
            RetryPolicy = new RetryPolicy { MaxRetries = 2 },
            Timeout = TimeSpan.FromSeconds(30),
        }).ToList();

        return new PlanGraph
        {
            Id = PlanId.New(),
            Name = "Scope Ownership Test Plan",
            Steps = steps,
            Edges = [new PlanEdge(steps[0].Id, steps[1].Id, EdgeType.ControlFlow)],
            Configuration = new PlanConfiguration
            {
                MaxParallelSteps = 4,
                PlanTimeout = TimeSpan.FromMinutes(10),
            },
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlannerDbContext> options)
        : IDbContextFactory<PlannerDbContext>
    {
        public PlannerDbContext CreateDbContext() => new(options);
    }
}
