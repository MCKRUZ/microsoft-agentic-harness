using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.Common.Helpers;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Governance;
using Domain.AI.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Tool that delegates a self-contained subtask to the best-fit specialized subagent, selected by
/// the deterministic capability-matching <see cref="ISupervisor"/>. This is the harness's equivalent
/// of a "spawn a governed subagent" capability: a skill that declares this tool can hand off work and
/// receive the subagent's result, with autonomy-tier enforcement, delegation-depth limits, and audit
/// applied by the supervisor.
/// </summary>
/// <remarks>
/// <para>
/// The supervisor — not the caller — chooses which subagent archetype handles the task, scoring
/// candidates on capability coverage and autonomy tier. The caller only describes the work and,
/// optionally, the capabilities it needs and the minimum autonomy tier the subagent must hold.
/// </para>
/// <para>
/// Delegations always start at depth 0 here; the supervisor increments and caps depth for any further
/// delegations a subagent issues, and built-in subagent profiles do not themselves carry this tool, so
/// unbounded recursion is not reachable through normal configuration.
/// </para>
/// </remarks>
public sealed class DelegateToSubagentTool : ITool
{
    /// <summary>The keyed-DI / SKILL.md name for this tool.</summary>
    public const string ToolName = "delegate_task";

    /// <summary>
    /// Maximum nesting of tool-initiated delegations. A spawned subagent can inherit this tool
    /// (some built-in subagent profiles inherit parent tools), so without a bound it could delegate
    /// recursively. Each <c>delegate_task</c> always enters the supervisor at depth 0, so this
    /// <see cref="AsyncLocal{T}"/> — which flows through the awaited subagent run — is what actually
    /// caps tool-driven recursion.
    /// </summary>
    private const int MaxDelegationDepth = 3;

    private const AutonomyLevel DefaultMinimumTier = AutonomyLevel.Supervised;

    private static readonly AsyncLocal<int> s_delegationDepth = new();

    private static readonly IReadOnlyList<string> Operations = ["delegate"];

    private readonly ISupervisor _supervisor;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly ILogger<DelegateToSubagentTool> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateToSubagentTool"/> class.
    /// </summary>
    /// <param name="supervisor">Runs the delegation, either capability-matched or to a named target.</param>
    /// <param name="ambientScope">
    /// Bridges to the calling request's own scope so <c>target_agent</c> (#518) can resolve the
    /// calling agent's own id for self-exclusion — see <c>.claude/rules/tools-and-mcp.md</c> for why
    /// this keyed-singleton tool resolves scoped state this way instead of taking it as a constructor
    /// dependency.
    /// </param>
    /// <param name="logger">Records delegation diagnostics.</param>
    public DelegateToSubagentTool(
        ISupervisor supervisor, IAmbientRequestScope ambientScope, ILogger<DelegateToSubagentTool> logger)
    {
        _supervisor = supervisor;
        _ambientScope = ambientScope;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Delegate a self-contained subtask to a subagent. Returns the subagent's result. " +
        "Parameters: 'task' (required) — what to do; " +
        "'target_agent' (optional) — the id of a specific registered peer agent to delegate to " +
        "directly, if your instructions list one whose description fits this task; when supplied, " +
        "'capabilities' and 'minimum_tier' are ignored and the named agent handles the task itself. " +
        "Without 'target_agent', the best-fit specialized subagent is chosen automatically: " +
        "'capabilities' (optional) — comma-separated tool names the subagent needs (e.g. \"file_system,document_search\"); " +
        "'minimum_tier' (optional) — one of Restricted, Supervised, Autonomous (default Supervised). " +
        "Use this to hand off a well-scoped piece of work rather than doing it inline.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.High;

    /// <inheritdoc />
    /// <remarks>
    /// Delegation is the closest fit among the three sink bits: it initiates further autonomous
    /// behaviour with the subagent's own tool access, which is what a supplied task description built
    /// from untrusted content would actually exploit — not a literal code-execution call, but the same
    /// shape of "content this agent read now drives further action."
    /// </remarks>
    public ToolCompositionCapability Capabilities => ToolCompositionCapability.ExecutesCode;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    /// <remarks>
    /// Delegation immediately runs an LLM-driven subagent turn. Whatever tools that subagent calls are
    /// separately governed on their own invocation — this declaration covers only what triggering the
    /// delegation itself requires.
    /// </remarks>
    public ToolCapability RequiredCapabilities => ToolCapability.LlmInvocation;

    /// <summary>
    /// Never directly invocable over HTTP. One call here is not one unit of work — it selects a
    /// subagent and runs it to completion, so the caller's single synchronous request expands into an
    /// open-ended sequence of model turns and whatever tools that subagent then reaches for.
    /// </summary>
    /// <remarks>
    /// The direct-invocation surface bounds a call with a wall-clock timeout, and a timeout is the
    /// wrong instrument for this: it cuts the caller's connection while the delegated work continues
    /// on the host's credentials, so the spend outlives the request that authorised it and nobody is
    /// left waiting for the result. Delegation belongs behind a job with a run record — the shape
    /// Track T reserves for it — not behind a synchronous tool call.
    /// </remarks>
    public bool IsDirectlyInvocable => false;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var task = GetString(parameters, "task");
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("The 'task' parameter is required and must describe the work to delegate.");

        var depth = s_delegationDepth.Value;
        if (depth >= MaxDelegationDepth)
            return ToolResult.Fail(
                $"Delegation depth limit ({MaxDelegationDepth}) reached; refusing to delegate further.");

        var targetAgent = GetString(parameters, "target_agent");
        var capabilities = ParseCapabilities(GetString(parameters, "capabilities"));
        var minimumTier = ParseTier(GetString(parameters, "minimum_tier"));

        // Increment around the awaited delegation so a subagent that re-invokes this tool observes the
        // deeper level via the flowing AsyncLocal, bounding tool-driven recursion.
        s_delegationDepth.Value = depth + 1;
        try
        {
            // #518: 'target_agent' takes over entirely when supplied — capabilities/minimum_tier
            // are meaningless once the caller has already named a specific peer.
            var result = !string.IsNullOrWhiteSpace(targetAgent)
                ? await _supervisor.DelegateToNamedAgentAsync(
                    targetAgent,
                    task,
                    callingAgentId: ResolveCallingAgentId(),
                    currentDelegationDepth: depth,
                    toolOverrides: null,
                    cancellationToken)
                : await _supervisor.DelegateAsync(
                    task,
                    capabilities,
                    minimumTier,
                    currentDelegationDepth: depth,
                    toolOverrides: null,
                    cancellationToken);

            if (result.IsSuccess)
            {
                _logger.LogDebug("Delegation succeeded ({Tokens} tokens, {DurationMs}ms)",
                    result.TokensUsed, result.DurationMs);
                return ToolResult.Ok(result.Output ?? string.Empty);
            }

            _logger.LogInformation("Delegation failed: {Reason}", result.FailureReason);
            return ToolResult.Fail(result.FailureReason ?? "Delegation failed for an unknown reason.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Delegation threw; surfacing as a tool failure");
            return ToolResult.Fail(SafeFailureText.For("Delegation failed", ex));
        }
        finally
        {
            s_delegationDepth.Value = depth;
        }
    }

    /// <summary>
    /// Resolves the calling agent's own id for <c>target_agent</c>'s self-exclusion (#518), best
    /// effort. <see langword="null"/> when no request scope is ambient — a direct invocation, or any
    /// caller outside a governed turn — in which case <see cref="ISupervisor.DelegateToNamedAgentAsync"/>
    /// skips self-exclusion rather than refusing the call; see that method's own remarks for why a
    /// missing identity is not treated as a self-delegation risk.
    /// </summary>
    private string? ResolveCallingAgentId()
        => _ambientScope.Current?.GetService<IAgentExecutionContext>()?.AgentId;

    private static string? GetString(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static IReadOnlyList<string> ParseCapabilities(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // Name-only: minimum_tier is a tool argument, so the model authors it. A numeric value would
    // parse to a tier that is not a member and be passed to the supervisor as the delegation
    // floor — where the fallback to DefaultMinimumTier is the behaviour actually wanted.
    private static AutonomyLevel ParseTier(string? raw)
        => EnumNameHelper.TryParseName<AutonomyLevel>(raw, out var tier)
            ? tier
            : DefaultMinimumTier;
}
