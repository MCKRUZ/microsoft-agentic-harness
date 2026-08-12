using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.RAG.Tests.Orchestration;

/// <summary>
/// Builds a <em>real</em> <see cref="ToolCallAdmissionPipeline"/> whose gates all permit, for the
/// retrieval fixtures whose subject is retrieval rather than admission.
/// </summary>
/// <remarks>
/// The real chain rather than a mock of <see cref="IToolCallAdmissionPipeline"/>, deliberately: a
/// mocked chain proves only that the executor called something, while the real one proves it reaches
/// the gates through the chain and in the chain's order.
/// </remarks>
internal static class PermissiveAdmission
{
    /// <summary>A chain that admits every call.</summary>
    public static ToolCallAdmissionPipeline Pipeline()
    {
        var governor = new Mock<IToolInvocationGovernor>();
        governor
            .Setup(g => g.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        var classificationGate = new Mock<IToolClassificationGate>();
        classificationGate
            .Setup(g => g.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ClassificationVerdict.Allow()));

        // Admits everything, matching the real gate's answer when tool authorization is switched off
        // — which is the default composition these retrieval fixtures run under.
        var authorizationGate = new Mock<IAgentToolAuthorizationGate>();
        authorizationGate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        // Never halts, and never returns null from Evaluate the way a loose mock would — the chain is
        // entitled to assume a verdict exists.
        var progress = new Mock<IProgressEvaluator>();
        progress
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());

        // A real, ungoverned recorder, so the chain reports the empty trace.
        var trace = new GovernanceTraceRecorder(
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
            Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default));

        return new ToolCallAdmissionPipeline(
            authorizationGate.Object,
            governor.Object,
            classificationGate.Object,
            Mock.Of<IToolCallObserverChain>(),
            progress.Object,
            trace,
            Mock.Of<IApprovalExecutionReporter>(),
            NullLogger<ToolCallAdmissionPipeline>.Instance);
    }
}
