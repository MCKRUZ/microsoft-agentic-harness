using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Bundles;
using Application.Common.Exceptions.ExceptionTypes;
using Domain.AI.Bundles;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Bundles.RunBundle;

/// <summary>
/// Handles <see cref="RunBundleCommand"/>: refuses when bundle execution is disabled or the handle is
/// unknown/expired, admits a <see cref="BundleRunStatus.Queued"/> run record carrying the resolved
/// capability envelope, and enqueues its job id for background dispatch. Returns the job id immediately —
/// the multi-turn conversation runs out-of-band on the <c>BundleRunBackgroundService</c>.
/// </summary>
/// <remarks>
/// The agent name is captured from the staged bundle here so the dispatcher never has to re-read the bundle
/// to know what to run. Admit-then-Enqueue: a host crash between the two leaves a queued record with no
/// worker to pick it up — the same non-durable loss profile as the in-memory job store and dispatch queue,
/// which is consistent with bundle runs not being persisted. Admission (<see cref="IBundleRunJobStore.TryCreate"/>)
/// refuses a second run against a conversation that already has one live, and refuses a caller already at
/// its concurrent-run cap — both decided and inserted as one atomic step, mirroring
/// <c>IRunJobStore.TryCreate</c>'s reasoning for workflow runs.
/// </remarks>
public sealed class RunBundleCommandHandler
    : IRequestHandler<RunBundleCommand, Result<RunBundleResult>>
{
    /// <summary>
    /// The single refusal message for an unknown handle, a handle belonging to someone else, and a
    /// conversation belonging to someone else.
    /// </summary>
    /// <remarks>
    /// One constant rather than three literals, because their being identical is a security property
    /// rather than a coincidence: a caller who can tell these apart can enumerate other people's
    /// handles and conversation ids by watching the response change. Kept in one place so that stays
    /// true by construction instead of by everyone remembering to copy it exactly.
    /// </remarks>
    private const string HandleNotFoundMessage =
        "Bundle handle not found or expired. Register the bundle again to obtain a fresh handle.";

    private readonly IBundleHandleStore _handleStore;
    private readonly IBundleRunJobStore _jobStore;
    private readonly IBundleRunDispatchQueue _dispatchQueue;
    private readonly IConversationStore _conversationStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RunBundleCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="RunBundleCommandHandler"/>.</summary>
    public RunBundleCommandHandler(
        IBundleHandleStore handleStore,
        IBundleRunJobStore jobStore,
        IBundleRunDispatchQueue dispatchQueue,
        IConversationStore conversationStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<RunBundleCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _handleStore = handleStore;
        _jobStore = jobStore;
        _dispatchQueue = dispatchQueue;
        _conversationStore = conversationStore;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RunBundleResult>> Handle(
        RunBundleCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.BundleExecution.Enabled)
        {
            return Result<RunBundleResult>.Forbidden(
                "Bundle execution is disabled. Set AppConfig.AI.BundleExecution.Enabled = true to enable it.");
        }

        // Owner check first: a caller can only run a handle they registered. A mismatch (or an absent handle)
        // is reported identically as not found, so the endpoint never reveals that a handle exists for
        // someone else.
        var owner = _handleStore.GetOwner(request.Handle);
        var staged = owner == request.OwnerId ? _handleStore.TryGet(request.Handle) : null;
        if (owner != request.OwnerId || staged is null)
        {
            return Result<RunBundleResult>.NotFound(HandleNotFoundMessage);
        }

        // Refuse a conversation belonging to someone else here, while the caller is still on the line.
        // The run itself would refuse it too — the store enforces ownership on every call the executor
        // makes — but only as an asynchronous failure the caller has to poll for, which reports a
        // permission decision as though the run had gone wrong.
        if (request.ConversationId is not null
            && !await CanUseConversationAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return Result<RunBundleResult>.NotFound(HandleNotFoundMessage);
        }

        var record = new BundleRunRecord
        {
            JobId = Guid.NewGuid().ToString("N"),
            Handle = request.Handle,
            OwnerId = request.OwnerId,
            AgentName = staged.Agent.Id,
            UserMessages = request.UserMessages,
            MaxTurns = request.MaxTurns,
            ConversationId = request.ConversationId,
            Envelope = WithBundleOwnedMcpServers(request.Envelope, staged),
            Status = BundleRunStatus.Queued,
            Streaming = request.Stream,
            CreatedAt = _time.GetUtcNow()
        };

        var admission = _jobStore.TryCreate(record, _config.CurrentValue.AI.BundleExecution.MaxActiveBundleRunsPerOwner);
        if (admission != BundleRunAdmission.Accepted)
        {
            return Result<RunBundleResult>.Conflict(
                "This conversation already has a live bundle run, or the caller holds the maximum "
                + "concurrent bundle runs allowed per owner.");
        }

        // A streaming run is NOT enqueued: its sole driver is the caller opening the stream endpoint, which
        // claims and drives it on the connection thread. Enqueuing it too would let the background dispatcher
        // race the stream for the same job. A non-streaming run is handed to the dispatcher to run poll-only.
        if (!request.Stream)
            await _dispatchQueue.EnqueueAsync(record.JobId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Created bundle run {JobId} for handle {Handle} (agent {AgentId}, {MessageCount} message(s), max {MaxTurns} turns, {Mode}).",
            record.JobId, record.Handle, record.AgentName, record.UserMessages.Count, record.MaxTurns,
            request.Stream ? "awaiting stream" : "queued for background dispatch");

        return Result<RunBundleResult>.Success(new RunBundleResult { JobId = record.JobId });
    }

    /// <summary>
    /// Additively unions the bundle's own registered MCP server names (<see cref="StagedBundle.McpServerNames"/>)
    /// into the caller's resolved envelope's <see cref="CapabilityEnvelope.AllowedMcpServers"/> — union
    /// only, never replace, so the caller's own configured grant is never narrowed by running a bundle.
    /// Also stamps the SAME names into <see cref="CapabilityEnvelope.BundleOwnedMcpServers"/> — the only
    /// place that field is ever populated, so "is this granted server bundle-owned" is answered from this
    /// run's own provenance, never re-derived downstream from a server name's shape (which a host-plugin
    /// server can share, since <c>PluginLoader</c> namespaces those identically). A bundle with no MCP
    /// servers (the common case) returns the envelope unchanged. The tool NAMES a bundle-owned server
    /// publishes are granted separately, once the server is actually reachable — see
    /// <c>BundleRunExecutor.RunConversationAsync</c>.
    /// </summary>
    private static CapabilityEnvelope WithBundleOwnedMcpServers(CapabilityEnvelope envelope, StagedBundle staged)
        => staged.McpServerNames.Count == 0
            ? envelope
            : envelope with
            {
                AllowedMcpServers = [.. envelope.AllowedMcpServers, .. staged.McpServerNames],
                BundleOwnedMcpServers = [.. envelope.BundleOwnedMcpServers, .. staged.McpServerNames]
            };

    /// <summary>
    /// True when this caller may run against the requested conversation — either because it is theirs,
    /// or because it does not exist yet and the run will create it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ownership decision is the store's, not this handler's: it is made by passing the caller to
    /// <see cref="IConversationStore.GetAsync"/> and letting it refuse. Nothing here compares owners —
    /// that comparison was hand-written in six places before it moved into the store, and this would
    /// have been the seventh.
    /// </para>
    /// <para>
    /// A refusal is reported as "not found", identically to an unknown handle, so a caller cannot use
    /// this endpoint to discover which conversation ids exist for other people.
    /// </para>
    /// </remarks>
    private async Task<bool> CanUseConversationAsync(
        RunBundleCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // Asking for a zero-message window, not the conversation. This needs to know only whether
            // the conversation exists and whether it is the caller's, and GetAsync would load the whole
            // transcript to answer that — on the synchronous request path, for a value thrown away. The
            // store guarantees a non-positive window returns NO messages rather than all of them, and
            // says so explicitly because the natural SQL translation would do the opposite; there are
            // contract tests on both implementations holding it to that.
            //
            // The result is discarded deliberately: absent is fine (the run creates it) and present is
            // fine (it is the caller's, or this would have thrown). Only the refusal carries meaning.
            await _conversationStore
                .GetHistoryForDispatch(request.ConversationId!, request.OwnerId, 0, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (ConversationAccessDeniedException)
        {
            // Deliberately the derived type, not UnauthorizedAccessException. The file-backed store
            // touches the file system, which raises the BASE type for an ACL or read-only failure that
            // has nothing to do with ownership — catching that would report an operator's permissions
            // problem to the caller as "no such conversation" and bury it.
            //
            // Not logged again: the store already recorded the caller, the conversation and its real
            // owner. A second line adds no fact and doubles every refusal in the audit trail.
            return false;
        }
    }
}
