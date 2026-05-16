using System.Text.Json;
using Domain.AI.Planner;
using FluentAssertions;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Infrastructure.AI.Tests.Persistence;

public sealed class PlannerDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlannerDbContext> _options;

    public PlannerDbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<PlannerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteVersionInterceptor())
            .Options;

        using var ctx = new PlannerDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    private PlannerDbContext CreateContext()
    {
        var ctx = new PlannerDbContext(_options);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return ctx;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void PlannerDbContext_Migrate_CreatesAllTables()
    {
        using var ctx = CreateContext();

        var tables = new List<string>();
        using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        tables.Should().Contain("PlanGraphs");
        tables.Should().Contain("PlanSteps");
        tables.Should().Contain("PlanEdges");
        tables.Should().Contain("StepExecutionStates");
        tables.Should().Contain("PlanExecutionLogs");
    }

    [Fact]
    public async Task PlanGraphEntity_Insert_PersistsAllFields()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var config = JsonSerializer.Serialize(new { PlanTimeout = "00:30:00", MaxParallelSteps = 10, MaxSubPlanDepth = 5 });

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = id,
            Name = "Test Plan",
            ConfigurationJson = config,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0
        });
        await ctx.SaveChangesAsync();

        await using var readCtx = CreateContext();
        var loaded = await readCtx.PlanGraphs.FindAsync(id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Test Plan");
        loaded.ConfigurationJson.Should().Be(config);
        loaded.CreatedAt.Should().Be(now);
        loaded.ParentPlanId.Should().BeNull();
    }

    [Fact]
    public async Task PlanGraphEntity_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setup = CreateContext())
        {
            setup.PlanGraphs.Add(new PlanGraphEntity
            {
                Id = id,
                Name = "Concurrent Plan",
                ConfigurationJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
                Version = 0
            });
            await setup.SaveChangesAsync();
        }

        // Two separate contexts simulate concurrent access
        await using var ctx1 = CreateContext();
        await using var ctx2 = CreateContext();

        var graph1 = await ctx1.PlanGraphs.FindAsync(id);
        var graph2 = await ctx2.PlanGraphs.FindAsync(id);

        graph1!.Name = "Updated by context 1";
        await ctx1.SaveChangesAsync();

        graph2!.Name = "Updated by context 2";
        var act = () => ctx2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task PlanStepEntity_ConfigJson_RoundTripsPolymorphicConfig()
    {
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = planId,
            Name = "Config Test Plan",
            ConfigurationJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var llmConfig = new LlmCallConfig
        {
            SystemPrompt = "You are helpful",
            ModelDeploymentKey = "gpt-4o"
        };
        var toolConfig = new ToolUseConfig
        {
            ToolName = "file_read",
            InputParameters = new Dictionary<string, object?> { ["path"] = "/tmp/test" }.AsReadOnly()
        };
        var humanConfig = new HumanGateConfig
        {
            EscalationMessage = "Approve this?",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };
        var branchConfig = new ConditionalBranchConfig
        {
            ConditionExpression = "output.score > 0.8",
            TrueEdgeTargetId = new PlanStepId(Guid.NewGuid()),
            FalseEdgeTargetId = new PlanStepId(Guid.NewGuid())
        };
        var subPlanConfig = new SubPlanConfig { IsolateContext = true };

        StepConfiguration[] configs = [llmConfig, toolConfig, humanConfig, branchConfig, subPlanConfig];
        var stepIds = new List<Guid>();

        foreach (var config in configs)
        {
            var stepId = Guid.NewGuid();
            stepIds.Add(stepId);
            ctx.PlanSteps.Add(new PlanStepEntity
            {
                Id = stepId,
                PlanGraphId = planId,
                Name = config.GetType().Name,
                Type = StepType.LlmCall,
                ConfigurationJson = JsonSerializer.Serialize<StepConfiguration>(config),
                RetryPolicyJson = "{}",
                TimeoutSeconds = 60
            });
        }
        await ctx.SaveChangesAsync();

        await using var readCtx = CreateContext();
        foreach (var (stepId, expected) in stepIds.Zip(configs))
        {
            var step = await readCtx.PlanSteps.FindAsync(stepId);
            step.Should().NotBeNull();

            var deserialized = JsonSerializer.Deserialize<StepConfiguration>(step!.ConfigurationJson);
            deserialized.Should().BeOfType(expected.GetType());
        }
    }

    [Fact]
    public async Task PlanStepEntity_ConfigJson_PreservesDiscriminator()
    {
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = planId,
            Name = "Discriminator Test",
            ConfigurationJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var stepId = Guid.NewGuid();
        var config = new ToolUseConfig
        {
            ToolName = "calculator",
            InputParameters = new Dictionary<string, object?> { ["expression"] = "2+2" }.AsReadOnly()
        };
        var json = JsonSerializer.Serialize<StepConfiguration>(config);

        ctx.PlanSteps.Add(new PlanStepEntity
        {
            Id = stepId,
            PlanGraphId = planId,
            Name = "Discriminator Step",
            Type = StepType.ToolUse,
            ConfigurationJson = json,
            RetryPolicyJson = "{}",
            TimeoutSeconds = 30
        });
        await ctx.SaveChangesAsync();

        await using var readCtx = CreateContext();
        var step = await readCtx.PlanSteps.FindAsync(stepId);

        using var doc = JsonDocument.Parse(step!.ConfigurationJson);
        doc.RootElement.GetProperty("type").GetString().Should().Be("tool_use");
    }

    [Fact]
    public async Task PlanEdgeEntity_ForeignKeys_EnforcedByDb()
    {
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = planId,
            Name = "FK Test Plan",
            ConfigurationJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        var nonExistentStepId = Guid.NewGuid();
        ctx.PlanEdges.Add(new PlanEdgeEntity
        {
            PlanGraphId = planId,
            FromStepId = nonExistentStepId,
            ToStepId = Guid.NewGuid(),
            Type = EdgeType.ControlFlow
        });

        var act = () => ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task StepExecutionStateEntity_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        var planId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var setup = CreateContext())
        {
            setup.PlanGraphs.Add(new PlanGraphEntity
            {
                Id = planId,
                Name = "Concurrency State Plan",
                ConfigurationJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
            setup.PlanSteps.Add(new PlanStepEntity
            {
                Id = stepId,
                PlanGraphId = planId,
                Name = "Step",
                Type = StepType.LlmCall,
                ConfigurationJson = "{}",
                RetryPolicyJson = "{}",
                TimeoutSeconds = 60
            });
            setup.StepExecutionStates.Add(new StepExecutionStateEntity
            {
                Id = stateId,
                StepId = stepId,
                Status = StepExecutionStatus.Pending,
                AttemptCount = 0,
                Version = 0
            });
            await setup.SaveChangesAsync();
        }

        await using var ctx1 = CreateContext();
        await using var ctx2 = CreateContext();

        var state1 = await ctx1.StepExecutionStates.FindAsync(stateId);
        var state2 = await ctx2.StepExecutionStates.FindAsync(stateId);

        state1!.Status = StepExecutionStatus.Running;
        await ctx1.SaveChangesAsync();

        state2!.Status = StepExecutionStatus.Running;
        var act = () => ctx2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task StepExecutionStateEntity_AttestationJson_RoundTripsNullable()
    {
        var planId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = planId,
            Name = "Attestation Test Plan",
            ConfigurationJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        ctx.PlanSteps.Add(new PlanStepEntity
        {
            Id = stepId,
            PlanGraphId = planId,
            Name = "Attestation Step",
            Type = StepType.ToolUse,
            ConfigurationJson = "{}",
            RetryPolicyJson = "{}",
            TimeoutSeconds = 60
        });

        var stateId = Guid.NewGuid();
        ctx.StepExecutionStates.Add(new StepExecutionStateEntity
        {
            Id = stateId,
            StepId = stepId,
            Status = StepExecutionStatus.Completed,
            AttemptCount = 1,
            AttestationJson = null
        });
        await ctx.SaveChangesAsync();

        await using var readCtx = CreateContext();
        var state = await readCtx.StepExecutionStates.FindAsync(stateId);
        state!.AttestationJson.Should().BeNull();

        // Now update with attestation JSON
        state.AttestationJson = """{"toolName":"calc","inputHash":"abc123","signature":"sig"}""";
        await readCtx.SaveChangesAsync();

        await using var verifyCtx = CreateContext();
        var updated = await verifyCtx.StepExecutionStates.FindAsync(stateId);
        updated!.AttestationJson.Should().Contain("calc");
    }

    [Fact]
    public async Task PlanExecutionLogEntity_AppendOnly_InsertsWithTimestamp()
    {
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = CreateContext();
        ctx.PlanGraphs.Add(new PlanGraphEntity
        {
            Id = planId,
            Name = "Log Test Plan",
            ConfigurationJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        ctx.PlanExecutionLogs.Add(new PlanExecutionLogEntity
        {
            PlanGraphId = planId,
            StepId = null,
            EventType = "PlanStarted",
            Timestamp = now,
            DetailsJson = """{"message":"Plan execution initiated"}"""
        });
        ctx.PlanExecutionLogs.Add(new PlanExecutionLogEntity
        {
            PlanGraphId = planId,
            StepId = Guid.NewGuid(),
            EventType = "StepStarted",
            Timestamp = now.AddSeconds(1),
            DetailsJson = null
        });
        await ctx.SaveChangesAsync();

        await using var readCtx = CreateContext();
        var logs = await readCtx.PlanExecutionLogs
            .Where(l => l.PlanGraphId == planId)
            .OrderBy(l => l.Id)
            .ToListAsync();

        logs.Should().HaveCount(2);
        logs[0].EventType.Should().Be("PlanStarted");
        logs[0].StepId.Should().BeNull();
        logs[0].DetailsJson.Should().Contain("initiated");
        logs[1].EventType.Should().Be("StepStarted");
        logs[1].Id.Should().BeGreaterThan(logs[0].Id);
    }
}
