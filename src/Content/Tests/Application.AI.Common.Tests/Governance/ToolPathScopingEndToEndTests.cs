using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Permissions;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DirectToolInvocation;
using Domain.Common.Config.AI.Permissions;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
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
    private const string DeniedPath = "C:/sandbox/secrets/creds.txt";
    private const string AllowedPath = "C:/sandbox/work/notes.txt";

    private static SandboxConfig DenyingSandboxConfig() => new()
    {
        ToolOverrides = new() { ["file_system"] = new ToolOverrideConfig { DeniedPaths = ["C:/sandbox/secrets"] } }
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
}
