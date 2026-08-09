using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.Logging.Abstractions;
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
            Mock.Of<IProgressEvaluator>(),
            NullLogger<ToolCallAdmissionPipeline>.Instance);

    /// <summary>
    /// An authorization gate that admits everything — the answer the real gate gives when
    /// <c>AI.Identity.ToolAuthorization.Enabled</c> is unset, which is the default composition.
    /// </summary>
    public static IAgentToolAuthorizationGate AuthorizationGate()
    {
        var gate = new Mock<IAgentToolAuthorizationGate>();
        gate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(AgentToolAuthorizationVerdict.Allow()));
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
