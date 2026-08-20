using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Services.Bundles;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Application.Core.CQRS.Agents.RunConversation;
using Domain.AI.Bundles;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// The shared engine that drives a single bundle run to a terminal state under its capability envelope and
/// ephemeral-agent overlay. Both triggers of a bundle run call it: the <see cref="BundleRunBackgroundService"/>
/// (async, poll-only runs) and the streaming endpoint (opt-in live runs). Concentrating the security-critical
/// ambient arming here is what stops the two triggers from diverging — see <see cref="IBundleRunExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering that preserves the invariants.</strong> The lease on the staged bundle is acquired
/// <em>before</em> the run is claimed <see cref="BundleRunStatus.Running"/>: a run whose handle expired before
/// it could start is failed straight from <see cref="BundleRunStatus.Queued"/> so it never carries a bogus
/// start time, and the lease then pins the staging directory against the cleanup sweeper for the whole run so
/// the ephemeral agent can read its skills from disk. The claim itself is an atomic
/// <see cref="IBundleRunJobStore.TryBeginRun"/> compare-and-set, so if two drivers race for the same job (two
/// stream connections, or a stream and the dispatcher) exactly one wins and drives it; the loser releases its
/// lease and reports <see cref="BundleRunExecutionStatus.AlreadyClaimed"/>.
/// </para>
/// <para>
/// <strong>Ambients.</strong> The capability envelope and overlay are re-published for the duration of the
/// drive; the envelope's presence is what makes the tool-invocation governor enforce, so omitting it would
/// fail the gate open. The scope wraps the whole <see cref="RunConversationCommand"/>, which returns a fully
/// materialised result — assistant text is streamed out-of-band through the ambient turn-stream sink while the
/// command runs, so there is no deferred enumeration outliving the scope.
/// </para>
/// </remarks>
public sealed class BundleRunExecutor : IBundleRunExecutor
{
    private readonly IBundleRunJobStore _jobStore;
    private readonly IBundleHandleStore _handleStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly IMcpToolProvider? _mcpToolProvider;
    private readonly IBundleMcpServerRegistrar? _mcpRegistrar;
    private readonly ILogger<BundleRunExecutor> _logger;

    /// <summary>Initializes a new <see cref="BundleRunExecutor"/>.</summary>
    /// <param name="mcpToolProvider">
    /// Optional — null on a host with no MCP client dependencies registered. Used only to discover the
    /// tool names a bundle's own registered MCP servers publish, so they can be additively granted for
    /// invocation; see <see cref="WithBundleOwnedToolGrantsAsync"/>.
    /// </param>
    /// <param name="mcpRegistrar">
    /// Optional — null on a host with no MCP client dependencies registered, matching
    /// <paramref name="mcpToolProvider"/>. Used only to tear down this run's own bundle-owned stdio MCP
    /// sessions once the run ends; see <see cref="RunConversationAsync"/>.
    /// </param>
    public BundleRunExecutor(
        IBundleRunJobStore jobStore,
        IBundleHandleStore handleStore,
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<BundleRunExecutor> logger,
        IMcpToolProvider? mcpToolProvider = null,
        IBundleMcpServerRegistrar? mcpRegistrar = null)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _jobStore = jobStore;
        _handleStore = handleStore;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
        _mcpToolProvider = mcpToolProvider;
        _mcpRegistrar = mcpRegistrar;
    }

    /// <inheritdoc />
    public async Task<BundleRunExecution> ExecuteAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var record = _jobStore.Get(jobId);
        if (record is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} was not found when dispatched (expired or swept before pickup); dropping.",
                jobId);
            return BundleRunExecution.NotFound;
        }

        // Only a queued run is ours to drive. Anything else was already claimed by another driver or has
        // already finished — report it back without touching it.
        if (record.Status != BundleRunStatus.Queued)
            return BundleRunExecution.AlreadyClaimed(record);

        // Acquire the handle BEFORE claiming Running: a run whose handle expired before pickup is failed
        // straight from Queued (never stamped with a start time). While the run holds the lease the sweeper
        // cannot delete the staging directory out from under the ephemeral agent.
        using var lease = _handleStore.Acquire(record.Handle);
        if (lease is null)
        {
            _logger.LogWarning(
                "Bundle run {JobId} could not start: handle {Handle} expired before the run began.",
                jobId, record.Handle);
            var failed = record with
            {
                Status = BundleRunStatus.Failed,
                Error = "The bundle handle expired before the run started.",
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(failed);
            return BundleRunExecution.Ran(failed);
        }

        // Atomically claim the run. If another driver won the race, stand down and release the lease. The
        // carried snapshot is the one already in hand — no caller reads it, so a fresh store read would be
        // wasted work on this rare race-loss path.
        var running = _jobStore.TryBeginRun(jobId, _time.GetUtcNow());
        if (running is null)
            return BundleRunExecution.AlreadyClaimed(record);

        return await DriveAsync(running, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BundleRunExecution> DriveAsync(
        BundleRunRecord running, IBundleHandleLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunConversationAsync(running, lease.Bundle, cancellationToken).ConfigureAwait(false);

            var succeeded = running with
            {
                Status = BundleRunStatus.Succeeded,
                Outcome = MapOutcome(result),
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(succeeded);
            return BundleRunExecution.Ran(succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or client disconnect — record the stop and propagate so a caller draining a queue
            // exits its loop. The streaming caller's connection is already gone.
            _jobStore.Update(running with
            {
                Status = BundleRunStatus.Failed,
                Error = "The run was cancelled.",
                CompletedAt = _time.GetUtcNow()
            });
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bundle run {JobId} failed with an unhandled exception.", running.JobId);
            var failed = running with
            {
                Status = BundleRunStatus.Failed,
                Error = "bundle_run.unhandled_exception",
                CompletedAt = _time.GetUtcNow()
            };
            _jobStore.Update(failed);
            return BundleRunExecution.Ran(failed);
        }
    }

    private async Task<ConversationResult> RunConversationAsync(
        BundleRunRecord record,
        StagedBundle staged,
        CancellationToken cancellationToken)
    {
        // Armed around the WHOLE run, not just the conversation below: WithBundleOwnedToolGrantsAsync
        // contacts the bundle's own MCP servers for tool discovery before either of the other two
        // ambients is armed, and that first contact is exactly where a bundle-owned stdio server's
        // session gets created and cached — it must see the same run id every later resolution inside
        // the conversation does, or McpConnectionManager has nothing to scope the session to.
        using (BundleRunIdAccessor.Begin(record.JobId))
        {
            try
            {
                var overlay = new EphemeralAgentOverlay
                {
                    Agent = staged.Agent,
                    OwnedSkills = staged.OwnedSkills
                };

                var envelope = await WithBundleOwnedToolGrantsAsync(record.Envelope, staged, cancellationToken)
                    .ConfigureAwait(false);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // Arm BOTH ambients for the whole conversation: the overlay so the ephemeral agent + its owned skills
                // resolve, and the envelope so the governor enforces the per-caller grant. Disposed in reverse when the
                // (materialised) conversation returns.
                using (EphemeralAgentOverlayAccessor.Begin(overlay))
                using (CapabilityEnvelopeAccessor.Begin(envelope))
                {
                    // A run that names a conversation continues it; one that does not gets an id of its own so
                    // its budget and telemetry still have somewhere to accumulate. The owner rides along only in
                    // the first case — it is what switches the shared loop into durable mode, so passing it for
                    // a self-contained run would make every one-shot run write a transcript nobody asked for.
                    var command = new RunConversationCommand
                    {
                        AgentName = record.AgentName,
                        UserMessages = record.UserMessages,
                        MaxTurns = record.MaxTurns,
                        ConversationId = record.ConversationId ?? record.JobId,
                        ConversationOwnerId = record.ConversationId is null ? null : record.OwnerId
                    };

                    return await mediator.Send(command, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Tears down this run's OWN stdio sessions — never the bundle's registration, which must
                // survive for the next run against the same staged handle. Idempotent and safe to call
                // unconditionally: a run that never contacted any of the bundle's servers, or one with
                // none declared, is a no-op. Runs even when the conversation threw, so a failed run never
                // leaks a container for the life of the handle's TTL.
                if (_mcpRegistrar is not null && staged.McpServerNames.Count > 0)
                {
                    await _mcpRegistrar.DisconnectRunScopedAsync(staged.McpServerNames, record.JobId)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Additively grants the NAMESPACED tool names a bundle's own MCP servers actually publish, so
    /// <c>ToolInvocationGovernor</c>'s invocation-time check — which matches a call against
    /// <see cref="CapabilityEnvelope.AllowedTools"/> by name, not by originating server — does not deny
    /// a call to a server the run's envelope already permits reaching. Contacts ONLY the bundle's own
    /// registered servers (<see cref="StagedBundle.McpServerNames"/>), never a host-shared server, once
    /// per run, before the envelope is armed. The stored <paramref name="envelope"/> (also used for
    /// status reporting elsewhere) is never mutated — a new value is returned for this run's ambient
    /// scope only. A server that fails to connect contributes zero tools this run — consistent with how
    /// <c>ToolChainBuilder</c> already treats an unreachable granted server — rather than failing the run.
    /// </summary>
    /// <remarks>
    /// <strong>Namespaced, not the bare server-declared name.</strong> Granting a bundle's self-reported
    /// tool name verbatim would let a malicious bundle advertise a tool literally named after a real,
    /// more privileged host tool and get it auto-granted by name coincidence — the invocation gate has
    /// no notion of which server a call resolves through, only the name. <see cref="BundleOwnedMcpToolNaming"/>
    /// namespaces the grant to this run's own server key, which only <c>ToolChainBuilder</c> ever
    /// publishes a tool under (via <c>NamespacedAIFunction</c>) for a bundle-owned resolution — so the
    /// granted name and the model-callable name always agree, and neither can ever collide with an
    /// unrelated host tool's bare name.
    /// </remarks>
    private async Task<CapabilityEnvelope> WithBundleOwnedToolGrantsAsync(
        CapabilityEnvelope envelope, StagedBundle staged, CancellationToken cancellationToken)
    {
        if (_mcpToolProvider is null || staged.McpServerNames.Count == 0)
            return envelope;

        // Concurrent, not sequential — a bundle that owns several servers must not pay their connect
        // latency cumulatively at the start of every run. Mirrors ToolChainBuilder.ResolveInjectedMcpToolsAsync's
        // Task.WhenAll fan-out over granted servers.
        var perServerNames = await Task.WhenAll(
            staged.McpServerNames.Select(serverName => DiscoverToolNamesAsync(serverName, cancellationToken)));

        var discoveredToolNames = perServerNames.SelectMany(names => names).ToList();

        return discoveredToolNames.Count == 0
            ? envelope
            : envelope with { AllowedTools = [.. envelope.AllowedTools, .. discoveredToolNames] };
    }

    /// <summary>
    /// Discovers one bundle-owned server's namespaced tool names, or an empty list if the server could
    /// not be reached — a failed server contributes zero tools rather than failing the whole run.
    /// </summary>
    private async Task<IReadOnlyList<string>> DiscoverToolNamesAsync(string serverName, CancellationToken cancellationToken)
    {
        var tools = await McpToolFetch.TryGetToolsAsync(
            _mcpToolProvider!, serverName, "Bundle run tool discovery", _logger, cancellationToken).ConfigureAwait(false);

        return tools is null
            ? []
            : tools.Select(tool => BundleOwnedMcpToolNaming.BuildToolName(serverName, tool.Name)).ToList();
    }

    private static BundleRunOutcome MapOutcome(ConversationResult result) => new()
    {
        ConversationSucceeded = result.Success,
        FinalResponse = result.FinalResponse,
        TurnCount = result.Turns.Count,
        TotalToolInvocations = result.TotalToolInvocations,
        BudgetExhausted = result.BudgetExhausted,
        ConversationError = result.Error
    };
}
