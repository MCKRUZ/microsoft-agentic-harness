using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Governance;
using Domain.AI.Governance;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// The service container both direct-invoker suites (<c>DirectToolInvokerTests</c> for keyed tools,
/// <c>DirectToolInvokerMcpTests</c> for MCP tools) build the invoker over: the scoped execution context,
/// a governor that records what it saw, every admission gate, a reporter double, the sanitizer, the
/// result store and the real admission chain (#827).
/// </summary>
/// <remarks>
/// <para>
/// It exists so the two suites cannot disagree about the composition. The registration block and the
/// recording governor were hand-copied between them, so a new required admission-chain dependency would
/// have been added to one suite and surfaced as a <c>GetRequiredService</c> failure in the other. It is
/// built on the real chain and the real scope machinery on purpose: these suites' whole subject is what
/// the invoker does before, during and after a call, and mocking the chain would move every governance
/// assertion off the code that actually runs.
/// </para>
/// <para>
/// <strong>Every gate is registered unconditionally.</strong> The invoker resolves the chain as a required
/// service and the chain requires all of them. An absent gate and one that permits everything are
/// indistinguishable at runtime and only one is safe, so a test that omitted one would be asserting
/// against a composition that cannot exist in production. A test that supplies none of its own gets
/// permissive ones, which is what a host with every feature off has.
/// </para>
/// </remarks>
internal static class DirectInvokerContainer
{
    /// <summary>
    /// Registers everything the invoker's admission chain needs. The caller adds whatever is specific to
    /// its suite (for example a keyed tool) and builds the provider.
    /// </summary>
    /// <param name="governor">Shared with the test, which cannot hold the scoped governor the container builds.</param>
    /// <param name="reporter">
    /// Closes the approval loop for whichever call the chain approves. Substituted for the real reporter
    /// because that needs the escalation subsystem's Infrastructure registrations, which an
    /// Application-layer fixture does not build; the chain still requires some registration.
    /// </param>
    /// <param name="sanitizer">
    /// The one sanitizer every pass runs through (the invoker owns none of its own, #487/#489). Defaults to
    /// a permissive one.
    /// </param>
    /// <param name="classificationGate">Defaults to a gate with nothing to classify.</param>
    /// <param name="observerChain">Defaults to a chain reporting no observers, so it is never asked for a decision.</param>
    /// <param name="appConfig">
    /// The configuration the chain reads its output ceilings from (#532). Defaults to the shipped values.
    /// </param>
    public static ServiceCollection Build(
        GovernorRecord governor,
        IApprovalExecutionReporter reporter,
        ICompositeResponseSanitizer? sanitizer = null,
        IToolClassificationGate? classificationGate = null,
        IToolCallObserverChain? observerChain = null,
        IOptionsMonitor<AppConfig>? appConfig = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolInvocationGovernor>(sp =>
            new RecordingGovernor(sp.GetRequiredService<IAgentExecutionContext>(), governor));

        services.AddSingleton(classificationGate ?? AdmissionHarness.PermissiveClassificationGate());
        services.AddSingleton(observerChain ?? Mock.Of<IToolCallObserverChain>());
        services.AddSingleton(AdmissionHarness.PermissiveProgressEvaluator());

        // The turn's governance trail: real rather than mocked, and required by the chain.
        services.AddSingleton<IGovernanceTraceRecorder>(AdmissionHarness.TraceRecorder());

        // Per-agent RBAC, permitting: the answer the real gate gives when tool authorization is off.
        services.AddSingleton(AdmissionHarness.PermissiveAuthorizationGate());

        services.AddSingleton(reporter);
        services.AddSingleton(sanitizer ?? AdmissionHarness.PermissiveSanitizer());

        // A real redaction filter, not a pass-through: a test asserts real redaction on the reported copy
        // independent of whatever the sanitizer did.
        services.AddSingleton<IContentRedactionFilter>(TestRedactionFilter.Instance);

        services.AddSingleton(appConfig ?? AdmissionHarness.Config());
        services.AddSingleton(AdmissionHarness.StubResultStore());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddToolCallAdmissionChain();

        return services;
    }
}

/// <summary>
/// Shared between a test and the scoped governor the container builds, since the test cannot hold the
/// scoped instance itself.
/// </summary>
internal sealed class GovernorRecord
{
    /// <summary>What the governor answers for every call; allow by default.</summary>
    public ToolInvocationDecision Decision { get; set; } = ToolInvocationDecision.Allow();

    /// <summary>The agent id the invocation's scoped context carried when the governor was asked.</summary>
    public string? AgentIdWhenAuthorizing { get; set; }

    /// <summary>The conversation id the scoped context carried when the governor was asked.</summary>
    public string? ConversationIdWhenAuthorizing { get; set; }

    /// <summary>The call-once scope id the scoped context carried when the governor was asked.</summary>
    public string? CallOnceScopeIdWhenAuthorizing { get; set; }

    /// <summary>The turn number the scoped context carried when the governor was asked.</summary>
    public int? TurnNumberWhenAuthorizing { get; set; }

    /// <summary>Makes authorization slow, so the deadline's reach over it is observable.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Makes the governor refuse every call with <paramref name="reason"/>.</summary>
    public void Deny(string reason) => Decision = ToolInvocationDecision.Deny(reason);
}

/// <summary>
/// A governor that answers as the test dictates and records what the world looked like when it was asked.
/// Resolved <em>from the invocation's own scope</em> and reading the same scoped
/// <see cref="IAgentExecutionContext"/> the real governor reads, so the arming assertions observe the
/// production mechanism rather than a stand-in the test wired up itself.
/// </summary>
internal sealed class RecordingGovernor(IAgentExecutionContext executionContext, GovernorRecord record)
    : IToolInvocationGovernor
{
    public async ValueTask<ToolInvocationDecision> AuthorizeAsync(
        string toolName, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? arguments = null,
        ToolCompositionTaint? composition = null,
        Domain.AI.Sandbox.ToolCallResourceRequest? resourceRequest = null)
    {
        record.AgentIdWhenAuthorizing = executionContext.AgentId;
        record.ConversationIdWhenAuthorizing = executionContext.ConversationId;
        record.CallOnceScopeIdWhenAuthorizing = executionContext.CallOnceScopeId;
        record.TurnNumberWhenAuthorizing = executionContext.TurnNumber;

        if (record.Delay > TimeSpan.Zero)
            await Task.Delay(record.Delay, cancellationToken);

        return record.Decision;
    }
}
