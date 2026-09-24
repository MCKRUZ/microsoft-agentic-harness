using System.Text.Json;
using Application.AI.Common.CQRS.Schedules;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Agent tool for listing, pausing, resuming, and deleting the caller's own recurring schedules
/// (#593). Deliberately does <strong>not</strong> support creating a schedule — see the remarks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Creation needs a fresh <c>CapabilityEnvelope</c>, which this tool cannot obtain.</strong>
/// <c>CreateScheduleCommand.Envelope</c> is resolved via <c>ICapabilityEnvelopeResolver.Resolve</c>,
/// which reads an authenticated <c>ClaimsPrincipal</c> — a concept that exists only at an HTTP
/// request boundary (see every controller that calls it: <c>WorkflowsController</c>,
/// <c>BundlesController</c>, <c>ToolsController</c>). An ordinary agent tool call has no
/// <c>ClaimsPrincipal</c> to resolve from — <c>CapabilityEnvelopeAccessor.Current</c> is null outside
/// a bundle run. Inventing a substitute resolution path here would be a new, unreviewed security
/// boundary; creation instead goes through <c>SchedulesController</c>, which resolves the envelope
/// exactly like every other run-starting endpoint. This tool covers everything that needs only
/// ownership, not a fresh grant.
/// </para>
/// <para>
/// Ownership (<c>OwnerId</c>/<c>TenantId</c>) is read from <see cref="IAmbientRequestScope"/>, per
/// <c>.claude/rules/tools-and-mcp.md</c>'s pattern for a keyed-singleton tool that needs the calling
/// request's own scoped state — mirroring <c>ToolResultFetchTool</c>. A caller with no resolvable
/// identity is refused rather than defaulted to an unowned/global schedule: an unattended, recurring
/// execution is exactly the case where "no scope" must never silently mean "everyone's".
/// </para>
/// </remarks>
public sealed class ManageSchedulesTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "manage_schedules";

    private const string List = "list";
    private const string Pause = "pause";
    private const string Resume = "resume";
    private const string Delete = "delete";

    private static readonly IReadOnlyList<string> Operations = [List, Pause, Resume, Delete];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IAmbientRequestScope _ambientScope;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManageSchedulesTool> _logger;

    public ManageSchedulesTool(
        IAmbientRequestScope ambientScope, IServiceScopeFactory scopeFactory, ILogger<ManageSchedulesTool> logger)
    {
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _ambientScope = ambientScope;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Lists, pauses, resumes, or deletes the caller's own recurring schedules. Does not create " +
        "schedules — creating one requires the host's HTTP schedule-submission endpoint, since it " +
        "grants the schedule its own execution permissions.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Medium;

    /// <inheritdoc />
    /// <remarks>
    /// Every operation dispatches through <c>IScheduleStore</c> (via the CQRS handlers in
    /// <c>Application.AI.Common.CQRS.Schedules</c>): <c>list</c> reads it, <c>pause</c>/<c>resume</c>/
    /// <c>delete</c> read-then-write it — the same "declare the union of what the operations actually
    /// touch" convention <c>DocumentIngestTool</c> uses.
    /// </remarks>
    public ToolCapability RequiredCapabilities => ToolCapability.DatabaseRead | ToolCapability.DatabaseWrite;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope();
        var ownerId = ScopeIdentity.Canonicalize(scope?.UserId);
        if (string.IsNullOrEmpty(ownerId))
            return ToolResult.Fail("No caller identity is available for this call; schedules cannot be managed anonymously.");

        var tenantId = ScopeIdentity.Canonicalize(scope?.TenantId);

        return operation.ToLowerInvariant() switch
        {
            List => await ListAsync(ownerId, tenantId, cancellationToken),
            Pause => await PauseOrResumeAsync(pause: true, ownerId, tenantId, parameters, cancellationToken),
            Resume => await PauseOrResumeAsync(pause: false, ownerId, tenantId, parameters, cancellationToken),
            Delete => await DeleteAsync(ownerId, tenantId, parameters, cancellationToken),
            _ => ToolResult.Fail($"Unknown operation: {operation}. Supported: {string.Join(", ", Operations)}")
        };
    }

    private IKnowledgeScope? ResolveScope() => _ambientScope.Current?.GetService<IKnowledgeScope>();

    private Task<ToolResult> ListAsync(string ownerId, string? tenantId, CancellationToken cancellationToken) =>
        MediatorDispatchRunner.RunAsync(
            _scopeFactory,
            async (mediator, ct) =>
            {
                var result = await mediator.Send(new ListSchedulesQuery { OwnerId = ownerId, TenantId = tenantId }, ct);
                return result.IsSuccess
                    ? ToolResult.Ok(JsonSerializer.Serialize(result.Value, JsonOptions))
                    : ToolResult.Fail($"Could not list schedules: {string.Join("; ", result.Errors)}");
            },
            _logger,
            ToolName,
            failureContext: "list",
            cancellationToken);

    private Task<ToolResult> PauseOrResumeAsync(
        bool pause, string ownerId, string? tenantId, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(parameters, "scheduleId", out var scheduleId, out var missing))
            return Task.FromResult(ToolResult.Fail(missing));
        if (!TryGetRequiredInt(parameters, "version", out var version, out var badVersion))
            return Task.FromResult(ToolResult.Fail(badVersion));

        return MediatorDispatchRunner.RunAsync(
            _scopeFactory,
            async (mediator, ct) =>
            {
                var result = pause
                    ? await mediator.Send(new PauseScheduleCommand { ScheduleId = scheduleId, OwnerId = ownerId, TenantId = tenantId, ExpectedVersion = version }, ct)
                    : await mediator.Send(new ResumeScheduleCommand { ScheduleId = scheduleId, OwnerId = ownerId, TenantId = tenantId, ExpectedVersion = version }, ct);

                return result.IsSuccess
                    ? ToolResult.Ok($"Schedule {scheduleId} {(pause ? "paused" : "resumed")}.")
                    : ToolResult.Fail(string.Join("; ", result.Errors));
            },
            _logger,
            ToolName,
            failureContext: pause ? "pause" : "resume",
            cancellationToken);
    }

    private Task<ToolResult> DeleteAsync(
        string ownerId, string? tenantId, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        if (!TryGetRequiredString(parameters, "scheduleId", out var scheduleId, out var missing))
            return Task.FromResult(ToolResult.Fail(missing));

        return MediatorDispatchRunner.RunAsync(
            _scopeFactory,
            async (mediator, ct) =>
            {
                var result = await mediator.Send(new DeleteScheduleCommand { ScheduleId = scheduleId, OwnerId = ownerId, TenantId = tenantId }, ct);
                return result.IsSuccess
                    ? ToolResult.Ok($"Schedule {scheduleId} deleted.")
                    : ToolResult.Fail(string.Join("; ", result.Errors));
            },
            _logger,
            ToolName,
            failureContext: "delete",
            cancellationToken);
    }

    private static bool TryGetRequiredString(
        IReadOnlyDictionary<string, object?> parameters, string key, out string value, out string error)
    {
        if (parameters.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
        {
            value = s;
            error = "";
            return true;
        }

        value = "";
        error = $"Required parameter '{key}' is missing or empty.";
        return false;
    }

    private static bool TryGetRequiredInt(
        IReadOnlyDictionary<string, object?> parameters, string key, out int value, out string error)
    {
        if (parameters.TryGetValue(key, out var raw))
        {
            switch (raw)
            {
                case int i:
                    value = i;
                    error = "";
                    return true;
                case long l when l is >= int.MinValue and <= int.MaxValue:
                    value = (int)l;
                    error = "";
                    return true;
                case long:
                    value = 0;
                    error = $"Parameter '{key}' is out of range for a schedule version.";
                    return false;
                case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed):
                    value = parsed;
                    error = "";
                    return true;
            }
        }

        value = 0;
        error = $"Required integer parameter '{key}' is missing or not a number.";
        return false;
    }
}
