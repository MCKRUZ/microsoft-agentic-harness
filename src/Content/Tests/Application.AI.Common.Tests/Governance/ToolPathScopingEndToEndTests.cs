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
using Domain.AI.Models;
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

    private static SandboxConfig DenyingSandboxConfig(string toolName = "file_system") => new()
    {
        ToolOverrides = new() { [toolName] = new ToolOverrideConfig { DeniedPaths = [$"{Root}sandbox/secrets"] } }
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
        IServiceProvider toolProvider, SandboxConfig sandboxConfig, IAgentExecutionContext context,
        IReadOnlySet<string>? toolNames = null)
    {
        var sandboxMonitor = Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == sandboxConfig);
        var lookup = new FirstPartyToolLookup(toolProvider, toolNames ?? new HashSet<string> { "file_system" });
        var resolver = new ToolPermissionProfileResolver(
            lookup, sandboxMonitor, NullLogger<ToolPermissionProfileResolver>.Instance);
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
            NullLogger<ToolInvocationGovernor>.Instance,
            new CapabilityEnvelopeGrantResolver(lookup, NullLogger<CapabilityEnvelopeGrantResolver>.Instance));

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
    public async Task AgentTurnPath_DeniedPath_OperationArrivesAsJsonElement_StillRefuses()
    {
        // Regression (#595): every other agent-turn test in this file passes "operation" as a plain
        // CLR string, so GovernedAIFunction.ReadOperation's JsonElement-unwrap arm — now delegating to
        // the shared ToolParameters.NormalizeScalar (extracted from three independent copies) — was
        // never actually exercised end-to-end by this path's own test suite. A caller outside the
        // standard Microsoft.Extensions.AI pipeline can still put a boxed JsonElement there directly.
        var (_, fileSystem, toolProvider) = BuildToolFixture();
        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(toolProvider, DenyingSandboxConfig(), context);

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var args = new AIFunctionArguments
        {
            ["operation"] = JsonSerializer.SerializeToElement("read"),
            ["parametersJson"] = JsonSerializer.SerializeToElement(new { path = DeniedPath })
        };
        var result = await aiFunction.InvokeAsync(args);

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
            lookup, Mock.Of<IOptionsMonitor<SandboxConfig>>(m => m.CurrentValue == sandboxConfig),
            NullLogger<ToolPermissionProfileResolver>.Instance);
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

    /// <summary>
    /// The workflow-submit producer's actual wire shape (correctness/security review on #587):
    /// System.Text.Json binds an <c>object?</c>-typed dictionary value to <see cref="JsonElement"/>
    /// with no converter registered to unwrap it, unlike <c>LlmPlanOutputMapper</c>'s
    /// <see cref="ReadConfig"/> shape, which already holds plain CLR strings.
    /// </summary>
    private static ToolUseConfig ReadConfigFromJsonElements(string path, string operation = "read") => new()
    {
        ToolName = "file_system",
        InputParameters = new Dictionary<string, object?>
        {
            ["operation"] = JsonSerializer.SerializeToElement(operation),
            ["path"] = JsonSerializer.SerializeToElement(path)
        }
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

    [Fact]
    public async Task PlanExecutorPath_JsonElementValuedArguments_DeniedPath_StillRefuses()
    {
        // Regression (correctness/security review on #587): a workflow submitted through the HTTP
        // surface arrives with JsonElement-valued arguments, not the plain strings LlmPlanOutputMapper
        // produces. Without normalization this silently degrades to "never scoped" for that producer —
        // fail-closed rather than a bypass, but not the "enforced identically" #587 set out to achieve.
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, trace) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);

        var step = BuildToolStep(ReadConfigFromJsonElements(DeniedPath));

        var result = await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Failed);
        result.IsPolicyDenial.Should().BeTrue();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlanExecutorPath_DeniedPath_ArgumentKeyCasingDiffers_StillRefuses()
    {
        // Regression (correctness review on #587): the agent-turn path hands Extract a case-insensitive
        // dictionary (ToolParameters.Flatten). A plan step naming the declared "path" parameter as
        // "Path" must still match — an ordinal dictionary here would silently miss it, resolve to
        // ToolCallResourceRequest.Empty, and CapabilityEnforcer reads Empty as "nothing to check".
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, trace) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);

        var step = BuildToolStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?> { ["operation"] = "read", ["Path"] = DeniedPath }
        });

        var result = await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Failed);
        result.IsPolicyDenial.Should().BeTrue();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlanExecutorPath_CaseVariantDuplicateArgumentKeys_LastWriterWins_CheckedValueMatchesDispatched()
    {
        // Regression (grader/correctness/security review on #587 rounds 3-4, root-caused on #595):
        // BuildToolArguments used to merge into an ORDINAL dictionary, so two keys that are
        // case-variants of each other could coexist as separate entries. Round 3's ToDictionary threw
        // ArgumentException uncaught on that collision; round 4's admission-layer last-write-wins fix
        // stopped the throw but only checked whichever value won, while RunSandboxAsync still
        // dispatched BOTH keys — a plan step could win the check with a decoy and have the tool still
        // read a denied value under its own declared casing.
        //
        // #595 fixed this at the actual source: BuildToolArguments now merges case-insensitively, so
        // the two keys collapse into ONE entry before either the resource check or the sandbox
        // dispatch ever sees them. There is no longer a second value for the tool to read that the
        // check didn't see: the same value is checked AND consumed, by construction — proven here
        // regardless of WHICH value survives the collapse. Which one wins depends on
        // config.InputParameters's own enumeration order (an implementation detail of whatever
        // IReadOnlyDictionary a producer supplies, not a contractual guarantee — code-review finding);
        // this test pins today's Dictionary-insertion-order behavior ("Path", declared second, wins)
        // without asserting that order is itself guaranteed. This test proves both halves — the
        // winning value is what gets checked (call succeeds, since it's in-bounds) AND it's the only
        // value serialized to the sandbox (the shadowed "path"/denied value never reaches dispatch).
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, _) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);
        sandboxExecutor
            .Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "hello world" });

        var step = BuildToolStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?>
            {
                ["operation"] = "read",
                ["path"] = DeniedPath,
                ["Path"] = AllowedPath // declared last in source order — wins the case-insensitive collapse
            }
        });

        var result = await executor.ExecuteAsync(step, new Dictionary<PlanStepId, string>(), CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Completed);
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(
                It.Is<SandboxExecutionRequest>(r => r.Input.Contains(AllowedPath) && !r.Input.Contains(DeniedPath)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlanExecutorPath_DeclaredParameterCollidesWithUpstreamKey_DeclaredWinsAcrossCasing()
    {
        // Regression (code-review on #595): the earlier collision test only covers two keys declared
        // together in the SAME step's own InputParameters. This covers the other shape the fix must
        // also hold for: a declared parameter colliding (case-insensitively) with a key an UPSTREAM
        // step's JSON output produces. BuildToolArguments's upstream-merge loop uses TryAdd specifically
        // so a declared parameter always wins over upstream-produced data of the same name — proven
        // here to hold across casing too, not just exact-name collisions.
        //
        // Deliberately asserts SUCCESS, not refusal: an upstream-merged value is always GetRawText()
        // quoted JSON text (#587/#595 item 5, tracked separately), so if the upstream decoy wrongly won
        // the collision, the call would ALSO be refused — just for an unrelated reason (the quoted text
        // fails path normalization), not because TryAdd worked. That shape would make an "expect
        // refusal" assertion pass regardless of which value won, hiding the exact bug this test exists
        // to catch (confirmed by mutation-testing: index-assignment instead of TryAdd here produced an
        // unexpected PASS against the original, refusal-based version of this test). Asserting success
        // with the DECLARED (in-bounds) value, and that the dispatched payload contains it while the
        // upstream decoy text never appears, is the one shape where the two outcomes genuinely diverge.
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, _) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);
        sandboxExecutor
            .Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult { Success = true, Output = "hello world" });

        const string upstreamDecoyText = "upstream-decoy-should-never-win";
        var step = BuildToolStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?> { ["operation"] = "read", ["path"] = AllowedPath }
        });
        var upstreamOutputs = new Dictionary<PlanStepId, string>
        {
            [new PlanStepId(Guid.NewGuid())] = JsonSerializer.Serialize(new { Path = upstreamDecoyText })
        };

        var result = await executor.ExecuteAsync(step, upstreamOutputs, CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Completed);
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(
                It.Is<SandboxExecutionRequest>(r => r.Input.Contains(AllowedPath) && !r.Input.Contains(upstreamDecoyText)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlanExecutorPath_OperationAndPathSourcedFromUpstreamOutput_RefusesViaRealPathScoping()
    {
        // Regression (code-review on #587, updated by #595): BuildToolArguments used to merge an
        // upstream step's JSON output via JsonElement.GetRawText() unconditionally, which kept the
        // literal JSON quote characters on string values — so an operation name sourced purely from
        // upstream output arrived as "\"read\"", not "read", and a denied path arrived the same way.
        // Before ResourceParameterExtractor.Extract's #587 fix, an operation that fails to match any
        // declared name returned ToolCallResourceRequest.Empty (not null), which CapabilityEnforcer
        // trusts as "nothing to check" and allows — silently defeating path scoping. #595 closed the
        // quoting mechanism itself for string values, so operation and path now arrive clean and are
        // correctly recognized — this call is refused through genuine path-scoping validation (the
        // path IS denied), not through the fallback "couldn't determine" safety net #587 added. Either
        // refusal reason proves the call didn't slip through; this asserts the one that is now correct.
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, trace) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);

        // Operation/path arrive only via the upstream merge, not the step's own declared parameters.
        var step = BuildToolStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?>()
        });
        var upstreamOutputs = new Dictionary<PlanStepId, string>
        {
            [new PlanStepId(Guid.NewGuid())] = JsonSerializer.Serialize(new { operation = "read", path = DeniedPath })
        };

        var result = await executor.ExecuteAsync(step, upstreamOutputs, CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Failed);
        result.IsPolicyDenial.Should().BeTrue();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlanExecutorPath_UpstreamChainedPathResolvesToEmptyString_StillRefuses()
    {
        // Regression (code-review on #595 item 5): fixing GetRawText()'s literal-quote bug for a
        // chained upstream string value (this PR) meant a genuinely empty string now reaches
        // ResourceParameterExtractor as a real 0-length string instead of the 2-char quoted garbage
        // that used to fail path validation. ResourceParameterExtractor.Extract's own value filter
        // used to treat an empty string identically to "parameter not supplied" (skipped, not added
        // to RequestedPaths) — which made CapabilityEnforcer.EnforcePathScoping see an empty request
        // and treat that as "nothing to check", passing the call with path scoping configured. The
        // fix keeps a present-but-empty value in the request so path validation denies it as
        // unparsable, same as any other invalid path.
        var fileSystem = new Mock<IFileSystemService>();
        var (executor, sandboxExecutor, trace) = BuildPlanExecutorFixture(DenyingSandboxConfig(), fileSystem);

        var step = BuildToolStep(new ToolUseConfig
        {
            ToolName = "file_system",
            InputParameters = new Dictionary<string, object?> { ["operation"] = "read" }
        });
        var upstreamOutputs = new Dictionary<PlanStepId, string>
        {
            [new PlanStepId(Guid.NewGuid())] = JsonSerializer.Serialize(new { path = "" })
        };

        var result = await executor.ExecuteAsync(step, upstreamOutputs, CancellationToken.None);

        result.Status.Should().Be(StepExecutionStatus.Failed);
        result.IsPolicyDenial.Should().BeTrue();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(d => d.Reason.Contains("path denied"));
        sandboxExecutor.Verify(
            s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Partial-declaration-map coverage on the two pre-existing paths (#595 item 2) ---

    /// <summary>
    /// Declares resource parameters for only ONE of its two supported operations — proving that
    /// <see cref="ResourceParameterExtractor.Extract"/>'s null-for-unrecognized-operation fix (#587
    /// code-review; originally verified only through <see cref="FileSystemTool"/>, which declares
    /// every operation it supports) also holds for a genuinely partial declaration map, on the two
    /// admission paths that predate #587 and had no dedicated test for this shape.
    /// </summary>
    private sealed class PartiallyDeclaredTool : ITool
    {
        public const string Name_ = "partial_tool";
        public string Name => Name_;
        public string Description => "Test tool with a partial ResourceParametersByOperation map.";
        public IReadOnlyList<string> SupportedOperations => ["declared_op", "undeclared_op"];

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>? ResourceParametersByOperation { get; } =
            new Dictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>
            {
                ["declared_op"] = new Dictionary<string, ResourceParameterKind> { ["path"] = ResourceParameterKind.Path }
            };

        public Task<ToolResult> ExecuteAsync(
            string operation, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }

    [Fact]
    public async Task AgentTurnPath_UndeclaredOperationOnPartiallyDeclaredTool_RefusesRatherThanAllows()
    {
        var tool = new PartiallyDeclaredTool();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(PartiallyDeclaredTool.Name_, tool);
        var toolProvider = services.BuildServiceProvider();

        var context = Mock.Of<IAgentExecutionContext>(c => c.AgentId == "test-agent");
        var (governor, trace) = BuildGovernor(
            toolProvider, DenyingSandboxConfig(PartiallyDeclaredTool.Name_), context,
            new HashSet<string> { PartiallyDeclaredTool.Name_ });

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, toolProvider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName([PartiallyDeclaredTool.Name_], "test-agent").Single();

        using var _ = ToolAdmissionAccessor.Begin(AdmissionHarness.Pipeline(governor: governor, executionContext: context));

        var args = new AIFunctionArguments
        {
            ["operation"] = "undeclared_op",
            ["parametersJson"] = JsonSerializer.SerializeToElement(new { path = AllowedPath })
        };
        var result = await aiFunction.InvokeAsync(args);

        // Before #587's code-review fix, an operation absent from ResourceParametersByOperation
        // entirely resolved to ToolCallResourceRequest.Empty ("nothing to check") and was ALLOWED —
        // even though this tool has real path scoping configured. It must now refuse.
        result.Should().BeOfType<string>();
        trace.Snapshot().ToolDecisions.Should().ContainSingle(
            d => d.Reason.Contains("no requested path could be determined"));
    }

    [Fact]
    public async Task DirectToolInvokerPath_UndeclaredOperationOnPartiallyDeclaredTool_RefusesRatherThanAllows()
    {
        var tool = new PartiallyDeclaredTool();
        var sandboxConfig = DenyingSandboxConfig(PartiallyDeclaredTool.Name_);

        GovernanceTraceRecorder? capturedTrace = null;

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(PartiallyDeclaredTool.Name_, tool);
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolCallAdmissionPipeline>(sp =>
        {
            var (governor, trace) = BuildGovernor(
                sp, sandboxConfig, sp.GetRequiredService<IAgentExecutionContext>(),
                new HashSet<string> { PartiallyDeclaredTool.Name_ });
            capturedTrace = trace;
            return AdmissionHarness.Pipeline(
                governor: governor, executionContext: sp.GetRequiredService<IAgentExecutionContext>(), trace: trace);
        });

        var provider = services.BuildServiceProvider();
        var invoker = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, [PartiallyDeclaredTool.Name_], NullLogger<ToolCatalog>.Instance),
            Mock.Of<IOptionsMonitor<DirectToolInvocationConfig>>(
                m => m.CurrentValue == new DirectToolInvocationConfig { Enabled = true }),
            NullLogger<DirectToolInvoker>.Instance);

        var request = new DirectToolInvocationRequest
        {
            ToolName = PartiallyDeclaredTool.Name_,
            Operation = "undeclared_op",
            Parameters = new Dictionary<string, object?> { ["path"] = AllowedPath },
            OwnerId = "caller-1",
            Envelope = new CapabilityEnvelope { AllowedTools = [PartiallyDeclaredTool.Name_] }
        };

        var outcome = await invoker.InvokeAsync(request, CancellationToken.None);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        capturedTrace.Should().NotBeNull();
        capturedTrace!.Snapshot().ToolDecisions.Should().ContainSingle(
            d => d.Reason.Contains("no requested path could be determined"));
    }
}
