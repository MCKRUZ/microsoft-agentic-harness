using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

/// <summary>
/// Regression tests for the solution-review finding that <see cref="ToolUseStepExecutor"/>
/// could resolve <see cref="SandboxIsolationLevel.None"/> for tools lacking a
/// <c>[ToolCapability]</c> attribute, even though no <see cref="ISandboxExecutor"/> is keyed
/// for <c>None</c> (only <c>Process</c> and <c>Container</c> are registered in production DI).
/// The executor must floor <c>None</c> to <c>Process</c> so the documented direct-execution
/// path runs instead of throwing an <see cref="InvalidOperationException"/> at keyed-service
/// resolution.
/// </summary>
public sealed class ToolUseStepExecutorSolutionReviewFixTests
{
    private readonly Mock<ICapabilityEnforcer> _capabilityEnforcer = new();
    private readonly Mock<IAttestationService> _attestationService = new();
    private readonly Mock<ICompositeResponseSanitizer> _responseSanitizer = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<ISandboxExecutor> _processExecutor = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly ToolUseStepExecutor _sut;

    public ToolUseStepExecutorSolutionReviewFixTests()
    {
        _notifier.Setup(n => n.NotifySandboxStatusAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<SandboxIsolationLevel>(),
            It.IsAny<ResourceUsage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _responseSanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((content, _) => SanitizationResult.Clean(content));

        // Mirror production DI (DependencyInjection.Planner.cs): only Process and Container
        // are keyed. There is deliberately NO executor registered for None — that is exactly
        // the gap the fix protects against.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, _processExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, new Mock<ISandboxExecutor>().Object);
        var sp = services.BuildServiceProvider();

        _sut = new ToolUseStepExecutor(
            _capabilityEnforcer.Object,
            sp,
            _attestationService.Object,
            _responseSanitizer.Object,
            _notifier.Object,
            _context,
            NullLogger<ToolUseStepExecutor>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ProfileIsolationNoneAndAutonomousStep_FloorsToProcessAndCompletes()
    {
        // Profile with MinimumIsolation = None reproduces ToolPermissionProfileResolver's
        // result for a tool that has no [ToolCapability] attribute (the production default).
        _capabilityEnforcer.Setup(c => c.ResolveProfileAsync("read_only_tool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.FileRead,
                MinimumIsolation = SandboxIsolationLevel.None
            });

        SandboxIsolationLevel? dispatchedLevel = null;
        _notifier.Setup(n => n.NotifySandboxStatusAsync(
                It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<SandboxIsolationLevel>(),
                It.IsAny<ResourceUsage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<PlanId, PlanStepId, string, SandboxIsolationLevel, ResourceUsage, string?, CancellationToken>(
                (_, _, _, level, _, _, _) => dispatchedLevel = level)
            .Returns(Task.CompletedTask);

        _processExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "ok", ResourceUsage = new ResourceUsage() });

        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "read-only-step",
            Type = StepType.ToolUse,
            Configuration = new ToolUseConfig { ToolName = "read_only_tool" },
            RetryPolicy = new RetryPolicy()
            // RequiredAutonomyLevel left null -> not Supervised/Restricted, so the old code
            // would pass None straight through to GetRequiredKeyedService and throw.
        };

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal(SandboxIsolationLevel.Process, dispatchedLevel);
        _processExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
