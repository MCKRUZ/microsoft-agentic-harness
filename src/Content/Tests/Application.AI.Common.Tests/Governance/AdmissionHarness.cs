using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
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
        IGovernanceTraceRecorder? trace = null) =>
        new(authorizationGate ?? PermissiveAuthorizationGate(),
            governor ?? PermissiveGovernor(),
            classificationGate ?? PermissiveClassificationGate(),
            observers ?? Mock.Of<IToolCallObserverChain>(),
            progressEvaluator ?? PermissiveProgressEvaluator(),
            trace ?? TraceRecorder(),
            Mock.Of<IApprovalExecutionReporter>(),
            NullLogger<ToolCallAdmissionPipeline>.Instance);

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
