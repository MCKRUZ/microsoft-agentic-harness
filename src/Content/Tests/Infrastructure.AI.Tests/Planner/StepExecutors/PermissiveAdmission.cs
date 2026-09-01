using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Context;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

/// <summary>
/// Builds a <em>real</em> <see cref="ToolCallAdmissionPipeline"/> whose gates all permit, for the step
/// executor fixtures whose subject is something other than admission.
/// </summary>
/// <remarks>
/// The real chain rather than a mock of <see cref="IToolCallAdmissionPipeline"/>, deliberately. A
/// mocked chain proves only that the executor called something; the real one proves the executor
/// reaches the gates through the chain and in the chain's order. That property was maintained by hand
/// in five separate places before this type existed, and was broken in one of them four times.
/// </remarks>
internal static class PermissiveAdmission
{
    /// <summary>A chain that admits every call, for fixtures that are not testing admission.</summary>
    public static ToolCallAdmissionPipeline Pipeline() => Pipeline(Mock.Of<IToolCallObserverChain>());

    /// <summary>A chain whose built-in gates admit every call, driven by the supplied observer chain.</summary>
    /// <param name="observers">The host-rule stage under test, or an empty chain.</param>
    public static ToolCallAdmissionPipeline Pipeline(IToolCallObserverChain observers)
    {
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        return PipelineOver(governor.Object, observers);
    }

    /// <summary>
    /// A chain driven by the caller's own governor, with the remaining gates permitting — for fixtures
    /// whose subject is what the governor decides.
    /// </summary>
    /// <param name="governor">The stage under test.</param>
    /// <param name="observers">The host-rule stage, or an empty chain when it is not the subject.</param>
    public static ToolCallAdmissionPipeline PipelineOver(
        IToolInvocationGovernor governor, IToolCallObserverChain? observers = null) =>
        new(AuthorizationGate(),
            governor,
            ClassificationGate(),
            observers ?? Mock.Of<IToolCallObserverChain>(),
            ProgressGuard(),
            CallOnceGate(),
            TraceRecorder(),
            Mock.Of<IApprovalExecutionReporter>(),
            PermissiveSanitizer(),
            PermissiveRedactionFilter(),
            Mock.Of<IOptionsMonitor<Domain.Common.Config.AppConfig>>(
                m => m.CurrentValue == new Domain.Common.Config.AppConfig()),
            NullLogger<ToolCallAdmissionPipeline>.Instance,
            StubExecutionContext(),
            StubResultStore());

    /// <summary>An execution context with a stable, non-null ToolResultScopeId (#521).</summary>
    public static IAgentExecutionContext StubExecutionContext(string toolResultScopeId = "test-scope") =>
        Mock.Of<IAgentExecutionContext>(c => c.ToolResultScopeId == toolResultScopeId);

    /// <summary>A result store that answers as if every result were small enough to keep inline.</summary>
    public static IToolResultStore StubResultStore()
    {
        var store = new Mock<IToolResultStore>();
        store
            .Setup(s => s.StoreIfLargeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string toolName, string? operation, string fullOutput, bool _, int? _, CancellationToken _) =>
                new ToolResultReference
                {
                    ResultId = Guid.NewGuid().ToString("N"),
                    ToolName = toolName,
                    Operation = operation,
                    PreviewContent = fullOutput,
                    FullContentPath = null,
                    SizeChars = fullOutput.Length,
                    Timestamp = DateTimeOffset.UtcNow
                });
        return store.Object;
    }

    /// <summary>A sanitizer that returns content unchanged — the answer a real one gives to clean text.</summary>
    public static ICompositeResponseSanitizer PermissiveSanitizer()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        return sanitizer.Object;
    }

    /// <summary>A redaction filter that returns content unchanged — nothing to scrub.</summary>
    public static IContentRedactionFilter PermissiveRedactionFilter()
    {
        var filter = new Mock<IContentRedactionFilter>();
        filter
            .Setup(f => f.Redact(It.IsAny<string>(), It.IsAny<IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory>>()))
            .Returns((string content, IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory> _) => content);
        return filter.Object;
    }

    /// <summary>A loop guard that never halts — what the real one answers while switched off.</summary>
    /// <remarks>
    /// Not <c>Mock.Of&lt;IProgressEvaluator&gt;()</c>: that returns null from <c>Evaluate</c>, and the
    /// chain is entitled to assume a verdict exists. These fixtures pass either way today only because
    /// no plan path opts into loop detection, which is a coincidence rather than a guarantee.
    /// </remarks>
    public static IProgressEvaluator ProgressGuard()
    {
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        return progress.Object;
    }

    /// <summary>A call-once gate that admits everything — no fixture here declares a tool call-once.</summary>
    /// <remarks>
    /// Not <c>Mock.Of&lt;ICallOnceGate&gt;()</c>, for the same reason as <see cref="ProgressGuard"/>
    /// and <see cref="ClassificationGate"/>: a loose mock's <c>EvaluateAsync</c> awaits to
    /// <see langword="null"/>, and the chain is entitled to assume a verdict exists.
    /// </remarks>
    public static ICallOnceGate CallOnceGate()
    {
        var gate = new Mock<ICallOnceGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));
        return gate.Object;
    }

    /// <summary>
    /// A real, ungoverned trace recorder, so the chain reports the empty trace rather than the null a
    /// loose mock would return.
    /// </summary>
    public static IGovernanceTraceRecorder TraceRecorder() =>
        new GovernanceTraceRecorder(
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
            Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default));

    /// <summary>
    /// An authorization gate that admits everything — the answer the real gate gives when
    /// <c>AI.Identity.ToolAuthorization.Enabled</c> is unset, which is the default composition.
    /// </summary>
    public static IAgentToolAuthorizationGate AuthorizationGate()
    {
        var gate = new Mock<IAgentToolAuthorizationGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));
        return gate.Object;
    }

    /// <summary>
    /// A classification gate with nothing to classify — the default, off, composition.
    /// </summary>
    /// <remarks>
    /// Not <c>Mock.Of&lt;IToolClassificationGate&gt;()</c>: that returns a default
    /// <c>ValueTask&lt;ClassificationVerdict&gt;</c>, which awaits to <see langword="null"/> and makes
    /// the chain throw on a verdict it is entitled to assume exists.
    /// </remarks>
    public static IToolClassificationGate ClassificationGate()
    {
        var classificationGate = new Mock<IToolClassificationGate>();
        classificationGate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ClassificationVerdict.Allow()));
        return classificationGate.Object;
    }
}
