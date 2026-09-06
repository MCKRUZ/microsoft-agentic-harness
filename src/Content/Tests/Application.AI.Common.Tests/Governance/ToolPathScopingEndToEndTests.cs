using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DirectToolInvocation;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// #418 end-to-end: a real <see cref="FileSystemTool"/>, converted through the real
/// <see cref="AIToolConverter"/> and <see cref="ToolChainBuilder"/>, admitted through a real
/// <see cref="ToolInvocationGovernor"/>/<see cref="CapabilityEnforcer"/> chain — proving the whole
/// wiring this feature added actually connects, not just each piece in isolation.
/// </summary>
public sealed class ToolPathScopingEndToEndTests
{
    // CapabilityEnforcer refuses any requested path that isn't OS-rooted (#418 CI hardening); a
    // literal "C:/..." path is rooted on Windows but not on Linux (CI runs on ubuntu-latest), so
    // every fixture below is built from this OS-correct root.
    private static readonly string Root = OperatingSystem.IsWindows() ? "C:/" : "/";
    private static readonly string DeniedPath = $"{Root}sandbox/secrets/creds.txt";
    private static readonly string AllowedPath = $"{Root}sandbox/work/notes.txt";

    private static SandboxConfig DenyingSandboxConfig() => new()
    {
        ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
    };

    /// <summary>A real <see cref="FileSystemTool"/> over a mocked <see cref="IFileSystemService"/>, keyed
    /// into a provider both the governor's tool lookup and <see cref="ToolChainBuilder"/> resolve from.</summary>
    private static (ITool Tool, Mock<IFileSystemService> FileSystem, IServiceProvider Provider) BuildToolFixture()
    {
        var fileSystem = new Mock<IFileSystemService>();
        var tool = new FileSystemTool(fileSystem.Object);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", tool);
        return (tool, fileSystem, services.BuildServiceProvider());
    }

    /// <summary>
    /// A real <see cref="ToolInvocationGovernor"/> wired to a real <see cref="CapabilityEnforcer"/>
    /// (itself backed by a real <see cref="ToolPermissionProfileResolver"/> reading <paramref name="sandboxConfig"/>),
    /// with every OTHER gate permissive — matching <c>ToolInvocationGovernorTests.Build</c>'s pattern, so
    /// this test isolates the path/host scoping wiring rather than re-proving every other gate. Also
    /// returns the trace recorder: the governor deliberately returns a generic model-facing denial for
    /// every refusal (never leaking capability/path detail to the LLM), so the specific reason — proof
    /// this was a path-scoping denial and not some other gate — is only observable via the governance
    /// trace, not the returned/reported message.
    /// </summary>
    private static (ToolInvocationGovernor Governor, GovernanceTraceRecorder Trace) BuildGovernor(
        IServiceProvider toolProvider, SandboxConfig sandboxConfig, IAgentExecutionContext context)
    {
        var sandboxMonitor = Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == sandboxConfig);
        var lookup = new FirstPartyToolLookup(toolProvider, new HashSet<string> { "file_system" });
        var resolver = new ToolPermissionProfileResolver(lookup, sandboxMonitor);
        var enforcer = new CapabilityEnforcer(resolver, NullLogger<CapabilityEnforcer>.Instance);

        var governance = new GovernanceConfig { EnforceToolInvocation = true, Enabled = false, EnableAudit = true };
        var governanceMonitor = Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == governance);
        var riskClassifier = Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == new ToolRiskProfile(BlastRadius.Low, true));
        var trace = new GovernanceTraceRecorder(governanceMonitor, riskClassifier);

        var permissions = new Mock<IToolPermissionService>();
        permissions
            .Setup(x => x.ResolvePermissionAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PermissionDecision.Allow("allowed by default"));

        var approvalRouter = new Mock<IToolApprovalRouter>();
        approvalRouter
            .Setup(x => x.RequestApprovalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<BlastRadius>(), It.IsAny<IReadOnlyDictionary<string, object?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.NotRouted("tool approval routing is disabled"));

        var governor = new ToolInvocationGovernor(
            context,
            permissions.Object,
            riskClassifier,
            Mock.Of<IToolBehaviorRegistry>(b => b.Resolve(It.IsAny<string>()) == ToolBehavior.Unknown),
            Mock.Of<IAutonomyDecisionEvaluator>(),
            Mock.Of<IGovernancePolicyEngine>(p => p.HasPolicies == false),
            Mock.Of<IGovernanceAuditService>(),
            Mock.Of<IDenialTracker>(),
            enforcer,
            approvalRouter.Object,
            trace,
            governanceMonitor,
            Mock.Of<IOptionsMonitor<PermissionsConfig>>(m => m.CurrentValue == new PermissionsConfig()),
            sandboxMonitor,
            NullLogger<ToolInvocationGovernor>.Instance);

        return (governor, trace);
    }

    private static AIFunctionArguments ReadArgs(string path, string operation = "read") => new()
    {
        ["operation"] = operation,
        ["parametersJson"] = JsonSerializer.SerializeToElement(new { path })
    };

    // --- Agent-turn path (GovernedAIFunction / ToolChainBuilder) ---

    [Fact]
    public async Task AgentTurnPath_DeniedPath_ParametersJsonArrivesAsRawString_StillRefuses()
    {
        // Regression: GovernedAIFunction.ReadParametersJson used to accept only a boxed JsonElement,
        // silently discarding any other CLR shape (including a plain, well-formed JSON string) as
        // "no parameters" — which downgrades to ToolCallResourceRequest.Empty and CapabilityEnforcer
        // trusts that and skips validating. AIFunctionArguments never carries a bare string here via
        // the real Microsoft.Extensions.AI pipeline, but a caller outside it could.
        var (_, fileSystem, toolProvider) = BuildToolFixture();
        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(toolProvider, DenyingSandboxConfig(), context);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var args = new AIFunctionArguments
        {
            ["operation"] = "read",
            ["parametersJson"] = JsonSerializer.Serialize(new { path = DeniedPath })
        };

        var result = await aiFunction.InvokeAsync(args);

        result.Should().BeOfType<string>();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        fileSystem.Verify(fs => fs.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AgentTurnPath_DeniedPath_RefusesBeforeFileSystemServiceReached()
    {
        var (_, fileSystem, toolProvider) = BuildToolFixture();
        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(toolProvider, DenyingSandboxConfig(), context);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var result = await aiFunction.InvokeAsync(ReadArgs(DeniedPath));

        // The model-facing text is deliberately generic (never leaks capability/path detail to the
        // LLM) — the trace is where the specific reason, and proof this was path scoping, lives.
        result.Should().BeOfType<string>();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        fileSystem.Verify(fs => fs.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AgentTurnPath_DeniedPath_OperationCasingDiffers_StillRefuses()
    {
        // Regression for the case-sensitivity bypass caught in review: AIToolConverter accepts an
        // operation case-insensitively and FileSystemTool.ExecuteAsync dispatches via
        // ToLowerInvariant(), so "Read" must be denied exactly like "read" — not silently pass because
        // ResourceParameterExtractor's declaration lookup missed on casing and returned Empty.
        var (_, fileSystem, toolProvider) = BuildToolFixture();
        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(toolProvider, DenyingSandboxConfig(), context);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var result = await aiFunction.InvokeAsync(ReadArgs(DeniedPath, operation: "Read"));

        result.Should().BeOfType<string>();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        fileSystem.Verify(fs => fs.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AgentTurnPath_AllowedPath_ReachesFileSystemAndSucceeds()
    {
        var (_, fileSystem, toolProvider) = BuildToolFixture();
        fileSystem.Setup(fs => fs.ReadFileAsync(AllowedPath, It.IsAny<CancellationToken>())).ReturnsAsync("hello world");
        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, _) = BuildGovernor(toolProvider, DenyingSandboxConfig(), context);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var result = await aiFunction.InvokeAsync(ReadArgs(AllowedPath));

        JsonSerializer.Serialize(result).Should().Contain("hello world");
        fileSystem.Verify(fs => fs.ReadFileAsync(AllowedPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Direct-invocation path (DirectToolInvoker) ---

    [Fact]
    public async Task DirectToolInvokerPath_DeniedPath_RefusesBeforeFileSystemServiceReached()
    {
        var fileSystem = new Mock<IFileSystemService>();
        var tool = new FileSystemTool(fileSystem.Object);
        var sandboxConfig = DenyingSandboxConfig();

        GovernanceTraceRecorder? capturedTrace = null;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", tool);
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolCallAdmissionPipeline>(sp =>
        {
            var (governor, trace) = BuildGovernor(sp, sandboxConfig, sp.GetRequiredService<IAgentExecutionContext>());
            capturedTrace = trace;
            return AdmissionHarness.Pipeline(
                governor: governor, executionContext: sp.GetRequiredService<IAgentExecutionContext>(), trace: trace);
        });

        var provider = services.BuildServiceProvider();
        var invoker = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, ["file_system"], NullLogger<ToolCatalog>.Instance),
            Mock.Of<IOptionsMonitor<DirectToolInvocationConfig>>(
                m => m.CurrentValue == new DirectToolInvocationConfig { Enabled = true }),
            NullLogger<DirectToolInvoker>.Instance);

        var request = new DirectToolInvocationRequest
        {
            ToolName = "file_system",
            Operation = "read",
            Parameters = new Dictionary<string, object?> { ["path"] = DeniedPath },
            OwnerId = "caller-1",
            Envelope = new CapabilityEnvelope { AllowedTools = ["file_system"] }
        };

        var outcome = await invoker.InvokeAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        capturedTrace.Should().NotBeNull();
        capturedTrace!.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        fileSystem.Verify(fs => fs.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DirectToolInvokerPath_DeniedPath_OperationCasingDiffers_StillRefuses()
    {
        // Same casing regression as the agent-turn path, exercised through the HTTP-facing surface —
        // the one the correctness/security review both called out as "most exposed to external callers".
        var fileSystem = new Mock<IFileSystemService>();
        var tool = new FileSystemTool(fileSystem.Object);
        var sandboxConfig = DenyingSandboxConfig();

        GovernanceTraceRecorder? capturedTrace = null;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", tool);
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolCallAdmissionPipeline>(sp =>
        {
            var (governor, trace) = BuildGovernor(sp, sandboxConfig, sp.GetRequiredService<IAgentExecutionContext>());
            capturedTrace = trace;
            return AdmissionHarness.Pipeline(
                governor: governor, executionContext: sp.GetRequiredService<IAgentExecutionContext>(), trace: trace);
        });

        var provider = services.BuildServiceProvider();
        var invoker = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, ["file_system"], NullLogger<ToolCatalog>.Instance),
            Mock.Of<IOptionsMonitor<DirectToolInvocationConfig>>(
                m => m.CurrentValue == new DirectToolInvocationConfig { Enabled = true }),
            NullLogger<DirectToolInvoker>.Instance);

        var request = new DirectToolInvocationRequest
        {
            ToolName = "file_system",
            Operation = "Read",
            Parameters = new Dictionary<string, object?> { ["path"] = DeniedPath },
            OwnerId = "caller-1",
            Envelope = new CapabilityEnvelope { AllowedTools = ["file_system"] }
        };

        var outcome = await invoker.InvokeAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        capturedTrace.Should().NotBeNull();
        capturedTrace!.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        fileSystem.Verify(fs => fs.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Plan/DAG executor path (ToolUseStepExecutor, #587) ---

    /// <summary>
    /// A second, independent <see cref="CapabilityEnforcer"/> — <see cref="ToolUseStepExecutor"/> takes
    /// its own <see cref="ICapabilityEnforcer"/> to resolve a sandbox isolation tier AFTER admission,
    /// separate from the one <see cref="BuildGovernor"/> wires into the admission chain itself. Both
    /// read the same <paramref name="sandboxConfig"/>, so they agree on what is denied.
    /// </summary>
    private static CapabilityEnforcer BuildCapabilityEnforcer(IServiceProvider toolProvider, SandboxConfig sandboxConfig)
    {
        var lookup = new FirstPartyToolLookup(toolProvider, new HashSet<string> { "file_system" });
        var resolver = new ToolPermissionProfileResolver(
            lookup, Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == sandboxConfig));
        return new CapabilityEnforcer(resolver, NullLogger<CapabilityEnforcer>.Instance);
    }

    /// <summary>
    /// A plan step's <see cref="ToolUseConfig.InputParameters"/> carries no separate operation field
    /// (#587) — the operation lives under the well-known <c>"operation"</c> key inside the flat
    /// argument set, the same convention <see cref="AIToolConverter"/> and
    /// <see cref="GovernedAIFunction"/> use for the other two paths.
    /// </summary>
    private static ToolUseConfig ReadConfig(string path, string operation = "read") => new()
    {
        ToolName = "file_system",
        InputParameters = new Dictionary<string, object?> { ["operation"] = operation, ["path"] = path }
    };

    private static PlanStep BuildToolStep(ToolUseConfig config) => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "tool-step",
        Type = StepType.ToolUse,
        Configuration = config,
        RetryPolicy = new RetryPolicy()
    };

    private static (ToolUseStepExecutor Executor, Mock<ISandboxExecutor> SandboxExecutor, GovernanceTraceRecorder Trace)
        BuildPlanExecutorFixture(SandboxConfig sandboxConfig, Mock<IFileSystemService> fileSystem)
    {
        var tool = new FileSystemTool(fileSystem.Object);
        var sandboxExecutor = new Mock<ISandboxExecutor>();

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", tool);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, sandboxExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, sandboxExecutor.Object);
        var provider = services.BuildServiceProvider();

        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(provider, sandboxConfig, context);
        var enforcer = BuildCapabilityEnforcer(provider, sandboxConfig);
        var lookup = new FirstPartyToolLookup(provider, new HashSet<string> { "file_system" });

        var executor = new ToolUseStepExecutor(
            enforcer,
            AdmissionHarness.Pipeline(governor: governor, executionContext: context, trace: trace),
            provider,
            Mock.Of<IAttestationService>(),
            Mock.Of<IPlanProgressNotifier>(),
            new PlanExecutionContext { CurrentPlanId = new PlanId(Guid.NewGuid()) },
            NullLogger<ToolUseStepExecutor>.Instance,
            lookup);

        return (executor, sandboxExecutor, trace);
    }

    [Fact]
    public async Task PlanExecutorPath_DeniedPath_RefusesBeforeSandboxDispatch()
    {
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, trace) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);

        var step = BuildToolStep(ReadConfig(DeniedPath));

        var result = await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        // Same fail-closed shape as the other two paths: the model-facing text is generic, and the
        // trace is where proof this was a path-scoping denial (not some other gate) lives.
        result.Status.Should().Be(StepExecutionStatus.Failed);
        result.IsPolicyDenial.Should().BeTrue();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlanExecutorPath_AllowedPath_ReachesSandboxDispatch()
    {
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, _) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);
        sandboxExecutor
            .Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "hello world" });

        var step = BuildToolStep(ReadConfig(AllowedPath));

        var result = await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        // The regression #587 guards against: an in-bounds path must not be refused just because this
        // admission path never determined its resource usage.
        result.Status.Should().Be(StepExecutionStatus.Completed);
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
