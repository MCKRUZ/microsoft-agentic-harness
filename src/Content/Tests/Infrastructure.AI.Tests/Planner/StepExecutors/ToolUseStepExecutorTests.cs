using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Attestation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class ToolUseStepExecutorTests
{
    private readonly Mock<ICapabilityEnforcer> _capabilityEnforcer = new();
    private readonly Mock<IAttestationService> _attestationService = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<ISandboxExecutor> _sandboxExecutor = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly ToolUseStepExecutor _sut;

    public ToolUseStepExecutorTests()
    {
        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifySandboxStatusAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<SandboxIsolationLevel>(),
            It.IsAny<ResourceUsage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capabilityEnforcer.Setup(c => c.ResolveProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolPermissionProfile { RequiredCapabilities = ToolCapability.FileRead });

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, _sandboxExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, _sandboxExecutor.Object);
        var sp = services.BuildServiceProvider();

        _sut = new ToolUseStepExecutor(
            _capabilityEnforcer.Object,
            sp,
            _attestationService.Object,
            _notifier.Object,
            _context,
            NullLogger<ToolUseStepExecutor>.Instance);
    }

    private static PlanStep CreateStep(ToolUseConfig config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "tool-step",
        Type = StepType.ToolUse,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_InvalidConfig_ReturnsFailed()
    {
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "bad",
            Type = StepType.ToolUse,
            Configuration = new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "y" },
            RetryPolicy = new RetryPolicy()
        };

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("invalid configuration type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulExecution_ReturnsCompleted()
    {
        var config = new ToolUseConfig { ToolName = "file_system" };
        var step = CreateStep(config);

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = true,
                Output = """{"files": ["a.txt"]}""",
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Contains("a.txt", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_FailedExecution_ReturnsFailed()
    {
        var config = new ToolUseConfig { ToolName = "file_system" };
        var step = CreateStep(config);

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = "Permission denied",
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal("Permission denied", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_AttestationVerificationFails_ReturnsFailed()
    {
        var config = new ToolUseConfig { ToolName = "file_system" };
        var step = CreateStep(config);
        var attestation = new ToolExecutionAttestation
        {
            ToolName = "file_system",
            InputHash = "abc",
            OutputHash = "def",
            Timestamp = DateTimeOffset.UtcNow,
            Signature = "sig",
            KeyVersion = "v1"
        };

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = true,
                Output = "result",
                Attestation = attestation,
                ResourceUsage = new ResourceUsage()
            });

        _attestationService.Setup(a => a.VerifyAsync(attestation, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("Attestation verification failed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_NeverDowngradesIsolation_WhenSupervisedAutonomy()
    {
        var config = new ToolUseConfig { ToolName = "dangerous_tool" };
        var step = new PlanStep
        {
            Id = new PlanStepId(Guid.NewGuid()),
            Name = "supervised-tool",
            Type = StepType.ToolUse,
            Configuration = config,
            RetryPolicy = new RetryPolicy(),
            RequiredAutonomyLevel = AutonomyLevel.Supervised
        };

        _capabilityEnforcer.Setup(c => c.ResolveProfileAsync("dangerous_tool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.FileRead,
                MinimumIsolation = SandboxIsolationLevel.Process
            });

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "ok", ResourceUsage = new ResourceUsage() });

        await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        _sandboxExecutor.Verify(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MergesUpstreamJsonIntoInput()
    {
        var config = new ToolUseConfig
        {
            ToolName = "calculator",
            InputParameters = new Dictionary<string, object?> { ["op"] = "add" }
        };
        var step = CreateStep(config);
        var upstreamId = new PlanStepId(Guid.NewGuid());
        var outputs = new Dictionary<PlanStepId, string> { [upstreamId] = """{"x": 5, "y": 3}""" };

        SandboxExecutionRequest? captured = null;
        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SandboxExecutionRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "8", ResourceUsage = new ResourceUsage() });

        await _sut.ExecuteAsync(step, outputs, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("\"op\"", captured!.Input);
        Assert.Contains("5", captured.Input);
        Assert.Contains("3", captured.Input);
    }
}
