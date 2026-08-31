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

        // Admits everything, matching the real gate's answer for a tool that was never declared
        // call-once — every tool these retrieval fixtures exercise.
        var callOnceGate = new Mock<ICallOnceGate>();
        callOnceGate
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(ToolInvocationDecision.Allow()));

        // A real, ungoverned recorder, so the chain reports the empty trace.
        var trace = new GovernanceTraceRecorder(
            Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
            Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default));

        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));

        var redactionFilter = new Mock<IContentRedactionFilter>();
        redactionFilter
            .Setup(f => f.Redact(It.IsAny<string>(), It.IsAny<IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory>>()))
            .Returns((string content, IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory> _) => content);

        return new ToolCallAdmissionPipeline(
            authorizationGate.Object,
            governor.Object,
            classificationGate.Object,
            Mock.Of<IToolCallObserverChain>(),
            progress.Object,
            callOnceGate.Object,
            trace,
            Mock.Of<IApprovalExecutionReporter>(),
            sanitizer.Object,
            redactionFilter.Object,
            // #532: the shipped PerResultCharLimit, so these orchestration tests see the real default
            // ceiling rather than an artificial one.
            Mock.Of<IOptionsMonitor<Domain.Common.Config.AppConfig>>(
                m => m.CurrentValue == new Domain.Common.Config.AppConfig()),
            NullLogger<ToolCallAdmissionPipeline>.Instance,
            Mock.Of<IAgentExecutionContext>(c => c.ToolResultScopeId == "test-scope"),
            StubResultStore());
    }

    /// <summary>A result store that answers as if every result were small enough to keep inline.</summary>
    private static IToolResultStore StubResultStore()
    {
        var store = new Mock<IToolResultStore>();
        store
            .Setup(s => s.StoreIfLargeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((string _, string toolName, string? operation, string fullOutput, int? _, CancellationToken _, bool _) =>
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
}
