using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Context;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// Builds a <em>real</em> <see cref="ToolCallAdmissionPipeline"/> from whichever gates a test cares
/// about, with the rest permitting.
/// </summary>
/// <remarks>
/// Tests of a caller (the governed tool wrapper, a step executor) drive the real chain rather than a
/// mock of <see cref="IToolCallAdmissionPipeline"/>. A mocked chain would prove only that the caller
/// asked something; the real chain proves the caller reaches the gates, through the chain, in the
/// chain's order — the property that used to be hand-maintained in five separate places.
/// </remarks>
internal static class AdmissionHarness
{
    /// <summary>Builds the chain, defaulting every gate this test did not supply to "permit".</summary>
    public static ToolCallAdmissionPipeline Pipeline(
        IToolInvocationGovernor? governor = null,
        IToolClassificationGate? classificationGate = null,
        IToolCallObserverChain? observers = null,
        IProgressEvaluator? progressEvaluator = null,
        IAgentToolAuthorizationGate? authorizationGate = null,
        ICallOnceGate? callOnceGate = null,
        IGovernanceTraceRecorder? trace = null,
        ICompositeResponseSanitizer? sanitizer = null,
        IContentRedactionFilter? redactionFilter = null,
        int? outputCeiling = null,
        int? aggregateLimit = null,
        IAgentExecutionContext? executionContext = null,
        IToolResultStore? resultStore = null) =>
        new(authorizationGate ?? PermissiveAuthorizationGate(),
            governor ?? PermissiveGovernor(),
            classificationGate ?? PermissiveClassificationGate(),
            observers ?? Mock.Of<IToolCallObserverChain>(),
            progressEvaluator ?? PermissiveProgressEvaluator(),
            callOnceGate ?? PermissiveCallOnceGate(),
            trace ?? TraceRecorder(),
            Mock.Of<IApprovalExecutionReporter>(),
            sanitizer ?? PermissiveSanitizer(),
            redactionFilter ?? PermissiveRedactionFilter(),
            Config(outputCeiling, aggregateLimit),
            NullLogger<ToolCallAdmissionPipeline>.Instance,
            executionContext ?? StubExecutionContext(),
            resultStore ?? StubResultStore());

    /// <summary>
    /// An execution context with a stable, non-null <see cref="IAgentExecutionContext.ToolResultScopeId"/>
    /// (#521) — what a test needs to exercise the spill path deterministically, since the real
    /// implementation's fallback is a fresh GUID per instance and a test asserting round-trip spill/
    /// retrieve behavior needs the SAME scope on both sides of that round trip.
    /// </summary>
    /// <param name="toolResultScopeId">The scope every spill/retrieve call in the test should share.</param>
    /// <param name="hasRetrievableToolResultScope">
    /// Defaults to <see langword="true"/> (#559) — most tests using this stub ARE exercising the spill
    /// path and need <c>SpillAndBuildMarkerAsync</c> to actually write, not silently no-op. A test
    /// specifically proving the no-op-when-unretrievable guard passes <see langword="false"/>.
    /// </param>
    public static IAgentExecutionContext StubExecutionContext(
        string toolResultScopeId = "test-scope", bool hasRetrievableToolResultScope = true) =>
        Mock.Of<IAgentExecutionContext>(c =>
            c.ToolResultScopeId == toolResultScopeId
            && c.HasRetrievableToolResultScope == hasRetrievableToolResultScope);

    /// <summary>
    /// A result store that answers every store request as if the result were small enough to keep
    /// inline (#521) — the permissive default for a test that isn't specifically exercising spill
    /// behavior. A test that IS should build its own mock or pass a real
    /// <c>FileSystemToolResultStore</c> pointed at a temp directory.
    /// </summary>
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

    /// <summary>
    /// A result store that answers every store request as if the result were actually persisted to
    /// disk (#521) — for a test that IS exercising spill behavior specifically, where
    /// <see cref="StubResultStore"/>'s "always inline" default would make the pipeline correctly
    /// decline to embed a retrieval id for a spill that never really happened.
    /// </summary>
    public static IToolResultStore PersistedResultStore()
    {
        var store = new Mock<IToolResultStore>();
        store
            .Setup(s => s.StoreIfLargeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string scopeId, string toolName, string? operation, string fullOutput, bool _, int? _, CancellationToken _) =>
                new ToolResultReference
                {
                    ResultId = Guid.NewGuid().ToString("N"),
                    ToolName = toolName,
                    Operation = operation,
                    PreviewContent = fullOutput[..Math.Min(20, fullOutput.Length)],
                    FullContentPath = $"/fake/{scopeId}/tool-results/persisted.json",
                    SizeChars = fullOutput.Length,
                    Timestamp = DateTimeOffset.UtcNow
                });
        return store.Object;
    }

    /// <summary>
    /// Config carrying the tool-output ceiling (#532) and the turn's aggregate output budget (#522),
    /// both defaulting to the shipped values so a test that does not care about bounding is unaffected
    /// by either.
    /// </summary>
    /// <remarks>
    /// A test that DOES care passes a small ceiling rather than building a 50,000-character result:
    /// the property under test is that the budget is enforced, not the size of the default.
    /// </remarks>
    public static IOptionsMonitor<AppConfig> Config(int? outputCeiling = null, int? aggregateLimit = null)
    {
        var config = new AppConfig();
        if (outputCeiling is { } ceiling)
            config.AI.ContextManagement.ToolResultStorage.PerResultCharLimit = ceiling;
        if (aggregateLimit is { } aggregate)
            config.AI.ContextManagement.ToolResultStorage.AggregatePerMessageCharLimit = aggregate;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(config);
        return monitor.Object;
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

    /// <summary>
    /// A sanitizer that replaces every occurrence of <paramref name="find"/> with
    /// <paramref name="replacement"/> and leaves everything else unchanged — a real sanitizer's answer
    /// when it recognizes something to scrub, without a test having to carry the injection-scrubbing
    /// logic itself. Centralized so a change to <c>Sanitize</c>'s signature only breaks one setup.
    /// </summary>
    /// <remarks>
    /// Reports <see cref="SanitizationResult.WasSanitized"/> accurately (true only when a replacement
    /// actually happened) rather than always answering <see cref="SanitizationResult.Clean"/> — callers
    /// of <c>ToolResultText.Sanitize</c> key their no-op fast path off that flag, not off whether the
    /// returned text differs from the input, so a mock that gets the flag wrong would make every caller
    /// silently skip reconstructing the sanitized result.
    /// </remarks>
    public static ICompositeResponseSanitizer SubstitutingSanitizer(string find, string replacement)
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) =>
            {
                var replaced = content.Replace(find, replacement);
                return replaced == content
                    ? SanitizationResult.Clean(content)
                    : new SanitizationResult(true, replaced, content, [], ThreatLevel.None);
            });
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

    /// <summary>
    /// A real trace recorder, ungoverned by default — the shipped composition, where nothing is
    /// enforced and the turn's trace comes back empty.
    /// </summary>
    /// <remarks>
    /// Real rather than mocked because the recorder is the thing the chain's <c>GetTrace</c> now
    /// answers from, and a mock of it would make every trace assertion a test of the mock.
    /// </remarks>
    /// <param name="governance">Governance config to run under; defaults to everything off.</param>
    public static GovernanceTraceRecorder TraceRecorder(GovernanceConfig? governance = null) =>
        new(Mock.Of<IOptionsMonitor<GovernanceConfig>>(
                m => m.CurrentValue == (governance ?? new GovernanceConfig())),
            Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default));

    /// <summary>
    /// An authorization gate that admits everything — what the real gate answers when
    /// <c>AI.Identity.ToolAuthorization.Enabled</c> is unset, which is the default composition.
    /// </summary>
    public static IAgentToolAuthorizationGate PermissiveAuthorizationGate()
    {
        var gate = new Mock<IAgentToolAuthorizationGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        return gate.Object;
    }

    /// <summary>An authorization gate that refuses every call, as an enabled gate does for an
    /// agent the allowlist does not cover.</summary>
    /// <param name="deniedMessage">The refusal text the gate returns.</param>
    public static Mock<IAgentToolAuthorizationGate> DenyingAuthorizationGate(string deniedMessage)
    {
        var gate = new Mock<IAgentToolAuthorizationGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Deny(deniedMessage));
        return gate;
    }

    /// <summary>
    /// A governor that refuses every call, as the real one does for a tool outside the caller's grant.
    /// </summary>
    /// <param name="deniedMessage">The refusal text the governor returns.</param>
    /// <remarks>
    /// The mirror of <see cref="DenyingAuthorizationGate"/>, and here for the same reason: the
    /// <c>AuthorizeAsync</c> setup carries a trailing optional argument, so every hand-rolled copy is
    /// another place to get the matcher shape wrong when that signature moves.
    /// </remarks>
    public static Mock<IToolInvocationGovernor> DenyingGovernor(string deniedMessage)
    {
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Deny(deniedMessage));
        return governor;
    }

    /// <summary>A governor that authorizes everything — the ungoverned default composition.</summary>
    public static IToolInvocationGovernor PermissiveGovernor()
    {
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        return governor.Object;
    }

    /// <summary>A classification gate with nothing to classify — the default, off, composition.</summary>
    public static IToolClassificationGate PermissiveClassificationGate()
    {
        var gate = new Mock<IToolClassificationGate>();
        gate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClassificationVerdict.Allow());
        return gate.Object;
    }

    /// <summary>A loop guard that never halts — what the real one answers while switched off.</summary>
    /// <remarks>
    /// Set up explicitly rather than left to <c>Mock.Of</c>: a loose mock returns null from
    /// <c>Evaluate</c>, and the chain is entitled to assume a verdict exists.
    /// </remarks>
    public static IProgressEvaluator PermissiveProgressEvaluator()
    {
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        return progress.Object;
    }

    /// <summary>
    /// A call-once gate that permits everything — what the real one answers when the tool being
    /// called was never declared call-once, which is every tool none of these tests declare one.
    /// </summary>
    public static ICallOnceGate PermissiveCallOnceGate()
    {
        var gate = new Mock<ICallOnceGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        return gate.Object;
    }

    /// <summary>A call-once gate that refuses every call, as the real one does for a tool already claimed.</summary>
    /// <param name="deniedMessage">The refusal text the gate returns.</param>
    public static Mock<ICallOnceGate> DenyingCallOnceGate(string deniedMessage)
    {
        var gate = new Mock<ICallOnceGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Deny(deniedMessage));
        return gate;
    }

    /// <summary>An observer chain that reports the rules the host registered and blocks on the verdict.</summary>
    /// <param name="verdict">What the chain answers for every call.</param>
    public static Mock<IToolCallObserverChain> ObserverChain(ToolInvocationDecision verdict)
    {
        var observers = new Mock<IToolCallObserverChain>();
        observers.SetupGet(o => o.HasObservers).Returns(true);
        observers
            .Setup(o => o.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(verdict));
        return observers;
    }
}
