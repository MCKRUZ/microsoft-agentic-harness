using Application.AI.Common;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.Core.Tests.Helpers;

/// <summary>
/// Registers the real tool-call admission chain over five permissive gates — not a mock of the
/// chain itself — so a hand-built test container resolves <c>ExecuteAgentTurnCommandHandler</c>
/// without asserting anything about governance behaviour.
/// </summary>
/// <remarks>
/// Extracted after this exact ~90-line block was found duplicated near-verbatim between
/// <c>AgentPipelineIntegrationTests</c> and <c>ZeroLlmPipelineTests</c> — the same
/// recurring-duplication shape <c>PermissiveAdmission</c> (in
/// <c>Infrastructure.AI.RAG.Tests</c>/<c>Infrastructure.AI.Tests</c>) already exists to prevent for
/// a directly-constructed <c>ToolCallAdmissionPipeline</c>. This is the <see cref="IServiceCollection"/>
/// DI-registration shape those two helpers don't cover, since this project builds the chain through
/// <c>AddToolCallAdmissionChain()</c> rather than constructing it directly.
/// </remarks>
public static class PermissiveGovernanceChain
{
    /// <summary>
    /// Registers the five admission gates as permissive mocks, plus the collaborators the real
    /// chain requires (config defaults, tool-result spill store), then calls
    /// <c>AddToolCallAdmissionChain()</c> so the container resolves the real chain over them.
    /// </summary>
    public static IServiceCollection AddPermissiveGovernanceChain(this IServiceCollection services)
    {
        var governorMock = new Mock<IToolInvocationGovernor>();
        governorMock
            .Setup(g => g.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, object?>?>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        services.AddScoped(_ => governorMock.Object);

        var progressMock = new Mock<IProgressEvaluator>();
        progressMock
            .Setup(p => p.Evaluate(It.IsAny<string>(), It.IsAny<Func<string?>>()))
            .Returns(ProgressVerdict.Continue());
        services.AddScoped(_ => progressMock.Object);

        services.AddSingleton(Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()));
        services.AddSingleton(Mock.Of<IToolRiskClassifier>(c => c.Classify(It.IsAny<string>()) == ToolRiskProfile.Default));

        var classificationMock = new Mock<IToolClassificationGate>();
        classificationMock
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClassificationVerdict.Allow());
        services.AddScoped(_ => classificationMock.Object);

        var observerChainMock = new Mock<IToolCallObserverChain>();
        observerChainMock.SetupGet(c => c.HasObservers).Returns(false);
        services.AddScoped(_ => observerChainMock.Object);

        var authorizationMock = new Mock<IAgentToolAuthorizationGate>();
        authorizationMock
            .Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolInvocationDecision.Allow());
        services.AddScoped(_ => authorizationMock.Object);

        services.AddScoped(_ => Mock.Of<IApprovalExecutionReporter>());

        var sanitizerMock = new Mock<ICompositeResponseSanitizer>();
        sanitizerMock
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        services.AddScoped(_ => sanitizerMock.Object);

        var redactionFilterMock = new Mock<IContentRedactionFilter>();
        redactionFilterMock
            .Setup(f => f.Redact(It.IsAny<string>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string content, IReadOnlyList<RedactionCategory> _) => content);
        services.AddScoped(_ => redactionFilterMock.Object);

        var toolCallReplayTreatmentMock = new Mock<IToolCallReplayTreatment>();
        toolCallReplayTreatmentMock.Setup(t => t.Enabled).Returns(true);
        toolCallReplayTreatmentMock
            .Setup(t => t.Treat(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string rawText, string? _) => rawText);
        toolCallReplayTreatmentMock.Setup(t => t.NoResultPlaceholder).Returns("[no result recorded]");
        // Explicit, because an unconfigured Moq int property returns 0 — and a zero limit here is
        // not "no limit", it drops every tool call.
        toolCallReplayTreatmentMock.Setup(t => t.MaxCallsPerTurn).Returns(32);
        toolCallReplayTreatmentMock.Setup(t => t.MaxReplayedChars).Returns(65536);
        services.AddScoped(_ => toolCallReplayTreatmentMock.Object);

        // The admission chain bounds tool output and reads its ceiling from AppConfig, so it
        // requires one — registered here rather than defaulted inside AddToolCallAdmissionChain so
        // a host that forgets to bind AppConfig fails loudly at container build (ValidateOnBuildSweepTests).
        services.AddSingleton<IOptionsMonitor<AppConfig>>(Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == new AppConfig()));
        services.AddSingleton(Mock.Of<IToolResultStore>());
        services.AddToolCallAdmissionChain();

        return services;
    }
}
