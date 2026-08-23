using System.Diagnostics;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolClassificationGate"/>: resolves the asset a tool call targets, classifies it
/// through the configured Purview provider, and applies the data-classification policy honoring the gate's
/// mode (off / audit / enforce).
/// </summary>
/// <remarks>
/// Scoped per turn (it reads the ambient <see cref="IAgentExecutionContext"/> for the audit identity), and
/// reached only as the second stage of <see cref="IToolCallAdmissionPipeline"/> — never called directly by
/// an execution path. Stateless across calls: every decision is emitted immediately to audit and OTel, so
/// no per-turn reset is required.
/// </remarks>
public sealed class DefaultToolClassificationGate : IToolClassificationGate
{
    private readonly IReadOnlyList<IAssetReferenceResolver> _resolvers;
    private readonly IDataClassificationProvider _provider;
    private readonly IClassificationPolicyEvaluator _evaluator;
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly IGovernanceAuditService _auditService;
    private readonly IAgentExecutionContext _executionContext;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly ILogger<DefaultToolClassificationGate> _logger;

    /// <summary>Initializes a new instance of the <see cref="DefaultToolClassificationGate"/> class.</summary>
    public DefaultToolClassificationGate(
        IEnumerable<IAssetReferenceResolver> resolvers,
        IDataClassificationProvider provider,
        IClassificationPolicyEvaluator evaluator,
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        IGovernanceAuditService auditService,
        IAgentExecutionContext executionContext,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        ILogger<DefaultToolClassificationGate> logger)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(redactionFilter);
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _resolvers = [.. resolvers];
        _provider = provider;
        _evaluator = evaluator;
        _sanitizer = sanitizer;
        _redactionFilter = redactionFilter;
        _auditService = auditService;
        _executionContext = executionContext;
        _governanceConfig = governanceConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ClassificationVerdict> EvaluateAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var governance = _governanceConfig.CurrentValue;
        var config = governance.DataClassification;

        // Opt-in: inert unless classification is switched on — no resolution, no provider call.
        if (config.Mode == ClassificationEnforcementMode.Off)
            return ClassificationVerdict.Allow();

        var enforcing = config.Mode == ClassificationEnforcementMode.Enforce;
        var asset = Resolve(toolName, arguments);

        AssetLabelResult label;
        try
        {
            label = await _provider.GetLabelAsync(asset, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The backend could not vouch for an asset that should be classified. Fail closed when
            // enforcing (block); observe-but-allow when only auditing.
            _logger.LogError(ex,
                "Classification lookup failed for tool {ToolName} (asset type {AssetType}); {Disposition}.",
                toolName, asset.Type, enforcing ? "blocking (fail-closed)" : "allowing (audit mode)");
            RecordDecision(toolName, "error", asset.Type, LabelSource.None, config.Mode, enforcing);
            _auditService.LogIfAuditEnabled(governance, _executionContext.AgentId, toolName, "classification:error");
            return enforcing ? ClassificationVerdict.Block(DeniedMessage(toolName)) : ClassificationVerdict.Allow();
        }

        var decision = _evaluator.Evaluate(label, config);
        RecordDecision(toolName, decision.Action.ToString(), asset.Type, label.Source, config.Mode, enforcing);
        _auditService.LogIfAuditEnabled(governance, _executionContext.AgentId, toolName, $"classification:{decision.Action}");

        // Audit mode records the would-be decision but never alters the call.
        if (!enforcing)
            return ClassificationVerdict.Allow();

        return decision.Action switch
        {
            ClassificationAction.Block => ClassificationVerdict.Block(DeniedMessage(toolName)),
            ClassificationAction.Redact => ClassificationVerdict.RedactOutput(),
            _ => ClassificationVerdict.Allow()
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// #484: a <c>Redact</c> verdict must do strictly more than the unconditional sanitize every other
    /// tool result already gets (<see cref="ToolCallAdmissionPipeline.ApplyOutputPolicy"/>) — otherwise
    /// an operator-configured classification policy is a control with no distinct effect. Routes through
    /// <see cref="ToolResultText.SanitizeAndRedact"/> rather than <see cref="ToolResultText.Sanitize"/>,
    /// which also applies <see cref="_redactionFilter"/>'s known-secret-pattern scrub. See
    /// <see cref="ToolResultText.Sanitize"/> for why the result's shape (raw string vs. serialized JSON
    /// string element) must survive the round trip, and why a structured result is left unchanged — such
    /// cases are better handled by a Block policy.
    /// </remarks>
    public object? RedactResult(string toolName, object? result) =>
        ToolResultText.SanitizeAndRedact(result, _sanitizer, _redactionFilter, toolName);

    private AssetReference Resolve(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        foreach (var resolver in _resolvers)
        {
            if (resolver.TryResolve(toolName, arguments, out var asset))
                return asset;
        }

        // No resolver claims this tool — it targets nothing Purview can classify, so the unknown-asset
        // policy applies.
        return AssetReference.Unknown();
    }

    private static void RecordDecision(
        string toolName, string action, AssetType assetType, LabelSource source,
        ClassificationEnforcementMode mode, bool enforced)
    {
        var tags = new TagList
        {
            { GovernanceConventions.ToolName, toolName },
            { GovernanceConventions.ClassificationActionTag, action },
            { GovernanceConventions.ClassificationAssetTypeTag, assetType.ToString() },
            { GovernanceConventions.ClassificationLabelSourceTag, source.ToString() },
            { GovernanceConventions.ClassificationModeTag, mode.ToString() },
            { GovernanceConventions.EnforcedTag, enforced }
        };
        GovernanceMetrics.ClassificationDecisions.Add(1, tags);
    }

    // Deliberately generic, and shared verbatim with every other gate via GovernanceDenials: the
    // detailed reason (label name, policy rule, asset path — even that a classification regime exists)
    // stays in the structured log, audit, and metric tags, never relayed to the model, so model-visible
    // content leaks no operator policy detail an adversary could probe — including which gate fired.
    private static string DeniedMessage(string toolName) =>
        GovernanceDenials.NotPermitted(toolName);
}
