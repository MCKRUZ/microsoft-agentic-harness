using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.Logging.Abstractions;
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

        return new ToolCallAdmissionPipeline(
            authorizationGate.Object,
            governor.Object,
            classificationGate.Object,
            Mock.Of<IToolCallObserverChain>(),
            Mock.Of<IProgressEvaluator>(),
            NullLogger<ToolCallAdmissionPipeline>.Instance);
    }
}
