using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Models.Sandbox;
using Application.AI.Common.Prompts.Interfaces;
using Docker.DotNet;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Workflow;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Infrastructure.AI.StateManagement;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Regression tests for composition-root wiring of the planner persistence layer.
/// Exercises the production <c>AddInfrastructureAIDependencies</c> registration rather
/// than a hand-built <see cref="DbContextOptions{TContext}"/>, so a missing
/// interceptor registration in <c>DependencyInjection.Planner.cs</c> is detected.
/// </summary>
public sealed class PlannerInterceptorWiringTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbDirectory;

    public PlannerInterceptorWiringTests()
    {
        _dbDirectory = Path.Combine(Path.GetTempPath(), "planner-wiring-" + Guid.NewGuid().ToString("N"));
        _provider = CreateServiceProvider(_dbDirectory);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_dbDirectory))
                Directory.Delete(_dbDirectory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite file may still be locked on some platforms; temp cleanup is best-effort.
        }
    }

    /// <summary>
    /// The DI-registered <see cref="IDbContextFactory{TContext}"/> must attach
    /// <see cref="SqliteVersionInterceptor"/> so the integer concurrency token actually
    /// increments. Without the interceptor wired in DI, two concurrent updates both write
    /// against <c>Version=0</c> and the second silently overwrites the first (no exception).
    /// This test fails on the old behavior and passes once the interceptor is registered.
    /// </summary>
    [Fact]
    public async Task PlannerDbContextFactory_ConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<PlannerDbContext>>();

        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var planId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var stateId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.PlanGraphs.Add(new PlanGraphEntity
            {
                Id = planId,
                Name = "Wiring Plan",
                ConfigurationJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
            seed.PlanSteps.Add(new PlanStepEntity
            {
                Id = stepId,
                PlanGraphId = planId,
                Name = "Step",
                Type = Domain.AI.Planner.StepType.LlmCall,
                ConfigurationJson = "{}",
                RetryPolicyJson = "{}",
                TimeoutSeconds = 60
            });
            seed.StepExecutionStates.Add(new StepExecutionStateEntity
            {
                Id = stateId,
                StepId = stepId,
                Status = Domain.AI.Planner.StepExecutionStatus.Pending,
                AttemptCount = 0,
                Version = 0
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx1 = await factory.CreateDbContextAsync();
        await using var ctx2 = await factory.CreateDbContextAsync();

        var state1 = await ctx1.StepExecutionStates.FindAsync(stateId);
        var state2 = await ctx2.StepExecutionStates.FindAsync(stateId);

        state1!.Status = Domain.AI.Planner.StepExecutionStatus.Running;
        await ctx1.SaveChangesAsync();

        state2!.Status = Domain.AI.Planner.StepExecutionStatus.Completed;
        var act = () => ctx2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// <see cref="IStateManager"/> must resolve from the container to the registered
    /// <see cref="CompositeStateManager"/>. Before the binding was added, no service mapped
    /// <see cref="IStateManager"/> to an implementation and any consumer injecting it failed
    /// to resolve.
    /// </summary>
    [Fact]
    public void StateManager_Interface_ResolvesToCompositeStateManager()
    {
        using var scope = _provider.CreateScope();

        var stateManager = scope.ServiceProvider.GetService<IStateManager>();

        stateManager.Should().NotBeNull().And.BeOfType<CompositeStateManager>();
    }

    private static ServiceProvider CreateServiceProvider(string dbDirectory)
    {
        Directory.CreateDirectory(dbDirectory);

        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Planner = new Domain.Common.Config.AI.Planner.PlannerOptions
                {
                    DatabasePath = Path.Combine(dbDirectory, "planner.db")
                }
            }
        };

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new AppConfigMonitorStub(appConfig));

        // External dependencies not registered by Infrastructure.AI itself.
        services.AddSingleton<ISender>(new Mock<ISender>().Object);
        services.AddSingleton<IPlanProgressNotifier>(new Mock<IPlanProgressNotifier>().Object);
        services.AddSingleton<ICapabilityEnforcer>(new Mock<ICapabilityEnforcer>().Object);
        services.AddSingleton<ICompositeResponseSanitizer>(new Mock<ICompositeResponseSanitizer>().Object);
        services.AddSingleton<IDockerClient>(new Mock<IDockerClient>().Object);
        services.AddSingleton<IPromptRegistry>(new Mock<IPromptRegistry>().Object);
        services.AddSingleton<IPromptRenderer>(new Mock<IPromptRenderer>().Object);
        services.AddSingleton<IPromptUsageRecorder>(new Mock<IPromptUsageRecorder>().Object);

        services.AddKnowledgeGraphDependencies(appConfig);
        services.AddInfrastructureAIDependencies(appConfig);

        return services.BuildServiceProvider();
    }

    private sealed class AppConfigMonitorStub(AppConfig config) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => config;
        public AppConfig Get(string? name) => config;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
