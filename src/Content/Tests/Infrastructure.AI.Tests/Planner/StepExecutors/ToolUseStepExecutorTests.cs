using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Governance;
using Domain.AI.Attestation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.Common.Config.AI;
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
    private readonly Mock<IToolInvocationGovernor> _toolGovernor = new();
    private readonly Mock<IToolClassificationGate> _classificationGate = new();
    private readonly Mock<IAttestationService> _attestationService = new();
    private readonly Mock<ICompositeResponseSanitizer> _responseSanitizer = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<ISandboxExecutor> _sandboxExecutor = new();
    private readonly PlanExecutionContext _context = new() { CurrentPlanId = new PlanId(Guid.NewGuid()) };
    private readonly ToolUseStepExecutor _sut;

    public ToolUseStepExecutorTests()
    {
        // Ungoverned default: no envelope armed and enforcement off means the governor allows.
        _toolGovernor.Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        // Classification off is the default composition: nothing to resolve, so nothing to block.
        _classificationGate.Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ClassificationVerdict.Allow()));

        _notifier.Setup(n => n.NotifyStepStartedAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<StepType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifySandboxStatusAsync(
            It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<SandboxIsolationLevel>(),
            It.IsAny<ResourceUsage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capabilityEnforcer.Setup(c => c.ResolveProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolPermissionProfile { RequiredCapabilities = ToolCapability.FileRead });

        _responseSanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((content, _) => SanitizationResult.Clean(content));

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, _sandboxExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, _sandboxExecutor.Object);
        var sp = services.BuildServiceProvider();

        _sut = new ToolUseStepExecutor(
            _capabilityEnforcer.Object,
            BuildAdmissionPipeline(Mock.Of<IToolCallObserverChain>()),
            sp,
            _attestationService.Object,
            _responseSanitizer.Object,
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
        // The sandbox's own text is never relayed — only the stable, scrubbed code. See
        // ExecuteAsync_FailedExecution_DoesNotLeakSandboxErrorText for why.
        Assert.Equal(PlanStepErrors.ToolFailed, result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_FailedExecution_DoesNotLeakSandboxErrorText()
    {
        // A sandbox failure message is raw process stderr, a raw exception message, or raw container
        // logs. StepExecutionState.ErrorMessage is persisted and handed back to callers through
        // PlanExecutionSummary.StepStates, which a host surface relays over HTTP — so anything a tool
        // printed on the way down (paths, environment variables, credentials) would leave the host.
        var config = new ToolUseConfig { ToolName = "file_system" };
        var step = CreateStep(config);
        const string PlantedSecret = "AZURE_CLIENT_SECRET=hunter2-do-not-leak";

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = $"sh: line 1: /home/runner/.config/app: {PlantedSecret}",
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(PlanStepErrors.ToolFailed, result.ErrorMessage);
        Assert.DoesNotContain("hunter2", result.ErrorMessage);
        Assert.DoesNotContain("/home/runner", result.ErrorMessage);
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
    public async Task ExecuteAsync_GovernorDenies_FailsStepWithoutTouchingSandbox()
    {
        // The governance gate must precede every execution resource: no permission profile is
        // resolved and no sandbox executor is invoked for a denied tool.
        const string deniedMessage = "Error: tool 'file_system' is not permitted in the current context.";
        _toolGovernor.Setup(g => g.AuthorizeAsync("file_system", It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Deny(deniedMessage)));

        var step = CreateStep(new ToolUseConfig { ToolName = "file_system" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(deniedMessage, result.ErrorMessage);
        _sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _capabilityEnforcer.Verify(
            c => c.ResolveProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ObserverBlocks_FailsStepWithoutTouchingSandbox()
    {
        // A plan step is a tool call like any other. If the governor were the only gate consulted
        // here, an agent could reach a tool the host's own rules refuse simply by emitting a ToolUse
        // plan step instead of calling the tool directly on the conversational path — the consumer's
        // rule would be registered, reported as active, and completely bypassable.
        const string observerDenial = "Error: tool 'file_system' is not permitted in the current context.";
        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Deny(observerDenial)));

        var sut = CreateExecutor(observers.Object);
        var step = CreateStep(new ToolUseConfig { ToolName = "file_system" });

        var result = await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(observerDenial, result.ErrorMessage);
        Assert.True(result.IsPolicyDenial);
        _sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ObserverSeesTheEffectiveArguments_NotJustTheDeclaredOnes()
    {
        // An observer's whole job is argument-sensitive judgement ("never wire over 10k"), so it must
        // see what the tool will actually receive. A value an upstream step produced is exactly the
        // kind a plan author did not write down, and exactly the kind a rule needs to catch.
        IReadOnlyDictionary<string, object?>? observed = null;
        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, CancellationToken>(
                (_, args, _) => observed = args)
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "ok", ResourceUsage = new ResourceUsage() });

        var sut = CreateExecutor(observers.Object);
        var step = CreateStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?> { ["declared"] = "yes" }
        });
        var upstream = new Dictionary<PlanStepId, string>
        {
            [new PlanStepId(Guid.NewGuid())] = """{"fromUpstream": "1000000"}"""
        };

        await sut.ExecuteAsync(step, upstream, CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("yes", observed!["declared"]);
        Assert.True(observed.ContainsKey("fromUpstream"));
    }

    [Fact]
    public async Task ExecuteAsync_ClassificationBlocks_FailsStepWithoutTouchingSandbox()
    {
        // A plan step now runs the data-classification gate, which it did not before. Until this
        // change a plan could hand a classified document to a tool where the identical call in a chat
        // turn was blocked or redacted — an agent could route around a data-loss control simply by
        // emitting a ToolUse step instead of calling the tool directly.
        const string classificationDenial = "Error: tool 'file_system' is not permitted: restricted data.";
        _classificationGate.Setup(g => g.EvaluateAsync(
                "file_system", It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ClassificationVerdict.Block(classificationDenial)));

        var step = CreateStep(new ToolUseConfig { ToolName = "file_system" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal(classificationDenial, result.ErrorMessage);
        Assert.True(result.IsPolicyDenial);
        _sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ClassificationSeesTheEffectiveArguments_NotJustTheDeclaredOnes()
    {
        // The gate resolves which asset a call touches from its arguments, so it must see what the
        // tool will actually receive. A path an upstream step produced is exactly the kind a plan
        // author did not write down, and exactly the kind a data-loss control needs to catch.
        IReadOnlyDictionary<string, object?>? seen = null;
        _classificationGate.Setup(g => g.EvaluateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, object?>, CancellationToken>((_, args, _) => seen = args)
            .Returns(ValueTask.FromResult(ClassificationVerdict.Allow()));

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "ok", ResourceUsage = new ResourceUsage() });

        var step = CreateStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?> { ["declared"] = "yes" }
        });
        var upstream = new Dictionary<PlanStepId, string>
        {
            [new PlanStepId(Guid.NewGuid())] = """{"fromUpstream": "/classified/report.docx"}"""
        };

        await _sut.ExecuteAsync(step, upstream, CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("yes", seen!["declared"]);
        Assert.True(seen.ContainsKey("fromUpstream"));
    }

    [Fact]
    public async Task ExecuteAsync_GovernorDenies_NeverConsultsObservers()
    {
        // Observers run only for calls the built-in gates already permitted. That ordering is what
        // makes an observer unable to widen access, and it also means a consumer's rule — which may
        // notify, bill, or call a model — is never run for a call that was refused anyway.
        _toolGovernor.Setup(g => g.AuthorizeAsync(
                "file_system", It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Deny("nope")));

        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);

        var sut = CreateExecutor(observers.Object);
        var step = CreateStep(new ToolUseConfig { ToolName = "file_system" });

        await sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        observers.Verify(
            o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private ToolUseStepExecutor CreateExecutor(IToolCallObserverChain observers)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, _sandboxExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, _sandboxExecutor.Object);

        return new ToolUseStepExecutor(
            _capabilityEnforcer.Object,
            BuildAdmissionPipeline(observers),
            services.BuildServiceProvider(),
            _attestationService.Object,
            _responseSanitizer.Object,
            _notifier.Object,
            _context,
            NullLogger<ToolUseStepExecutor>.Instance);
    }

    /// <summary>
    /// Builds the REAL admission chain over this fixture's gate mocks, rather than mocking the chain
    /// itself. Every gate assertion below therefore also proves that this execution path reaches the
    /// gates through the chain, in the chain's order — which is the property that used to be
    /// maintained by hand in five places and kept being broken in one of them.
    /// </summary>
    private ToolCallAdmissionPipeline BuildAdmissionPipeline(IToolCallObserverChain observers) =>
        new(_toolGovernor.Object,
            _classificationGate.Object,
            observers,
            Mock.Of<IProgressEvaluator>());

    [Fact]
    public async Task ExecuteAsync_GovernorAllows_AuthorizesExactToolName()
    {
        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "ok", ResourceUsage = new ResourceUsage() });

        var step = CreateStep(new ToolUseConfig { ToolName = "file_system" });

        var result = await _sut.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        _toolGovernor.Verify(g => g.AuthorizeAsync("file_system", It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()), Times.Once);
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
