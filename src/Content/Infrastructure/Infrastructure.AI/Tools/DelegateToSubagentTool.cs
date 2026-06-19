using Application.AI.Common.Interfaces.Agents;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Models;
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
    private readonly ILogger<DelegateToSubagentTool> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateToSubagentTool"/> class.
    /// </summary>
    public DelegateToSubagentTool(ISupervisor supervisor, ILogger<DelegateToSubagentTool> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Delegate a self-contained subtask to the best-fit specialized subagent (the supervisor picks it). " +
        "Returns the subagent's result. Parameters: 'task' (required) — what to do; " +
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
    public bool IsConcurrencySafe => false;

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

        var capabilities = ParseCapabilities(GetString(parameters, "capabilities"));
        var minimumTier = ParseTier(GetString(parameters, "minimum_tier"));

        // Increment around the awaited delegation so a subagent that re-invokes this tool observes the
        // deeper level via the flowing AsyncLocal, bounding tool-driven recursion.
        s_delegationDepth.Value = depth + 1;
        try
        {
            var result = await _supervisor.DelegateAsync(
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
            return ToolResult.Fail($"Delegation failed: {ex.Message}");
        }
        finally
        {
            s_delegationDepth.Value = depth;
        }
    }

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

    private static AutonomyLevel ParseTier(string? raw)
        => Enum.TryParse<AutonomyLevel>(raw, ignoreCase: true, out var tier)
            ? tier
            : DefaultMinimumTier;
}
