using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Models.Conversations;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// Deletes conversations nobody has written to for a configured period, together with everything that belongs to them
/// (#901), so a conversation is not kept forever by default.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One conversation at a time, in erasure order.</strong> A conversation is removed the way right-to-erasure
/// removes a person's, through the same <see cref="IPersonalDataEraser"/> stores and the same stages: what is found
/// through the conversation first, then the conversation, then what is only safe to remove once it is gone (the
/// call-once ledger). Only stores whose <see cref="IPersonalDataEraser.ErasureReach"/> is
/// <see cref="ErasureReach.NamedConversations"/> are asked; an owner-wide store would take the rest of the person's data
/// with it.
/// </para>
/// <para>
/// <strong>Never a conversation in use.</strong> The sweep takes the conversation's turn lease before touching it, the
/// same lease every turn takes, so no turn can start while it is being deleted. A conversation whose lease does not come
/// free within a few seconds has a turn running and is skipped. Holding the lease, the sweep asks again whether the
/// conversation is still idle, because a turn may have finished between the listing and the lease, and it stops if the
/// lease is lost. The conversation itself is deleted through <see cref="IIdleConversationSource.DeleteIfIdleAsync"/>,
/// which re-checks the owner and the idle time in the same step as the delete, so a conversation deleted and started again
/// under the same id is never the one removed; the store's own conversation eraser, written for owner erasure, has no
/// such condition and is not used here. When that delete removes nothing, the stage after it does not run.
/// </para>
/// <para>
/// <strong>A failure keeps the conversation.</strong> If any store fails before the conversation is deleted, the
/// conversation stays and the next sweep tries again, finding the same records through it. A failure after it is
/// deleted (the call-once ledger) can only be logged: nothing leads to those rows any more.
/// </para>
/// <para>
/// Each sweep ends with every <see cref="IPersonalDataScrubber"/> among those stores clearing the deleted bytes from its
/// files, so an expired transcript does not stay readable on disk.
/// </para>
/// </remarks>
public sealed class ConversationExpiry : IConversationExpiry
{
    /// <summary>The shortest idle period accepted. Anything shorter is a configuration mistake, not a policy.</summary>
    public static readonly TimeSpan MinimumIdlePeriod = TimeSpan.FromDays(1);

    /// <summary>How long to wait for a conversation's turn lease before treating it as in use.</summary>
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<IPersonalDataEraser> _erasers;
    private readonly IIdleConversationSource? _source;
    private readonly IConversationTurnLease _lease;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationExpiry> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConversationExpiry"/> class.</summary>
    /// <param name="erasers">Every registered store that can erase; only the conversation-scoped ones are used.</param>
    /// <param name="sources">
    /// The conversation store, when it can list idle conversations. A forwarded registration yields
    /// <see langword="null"/> for a store that cannot, and then nothing expires.
    /// </param>
    /// <param name="lease">The turn lease every turn takes, so expiry never runs beside one.</param>
    /// <param name="timeProvider">The clock the idle cutoff and the lease wait are measured by.</param>
    /// <param name="logger">Receives skipped and failed conversations, by id only.</param>
    public ConversationExpiry(
        IEnumerable<IPersonalDataEraser> erasers,
        IEnumerable<IIdleConversationSource?> sources,
        IConversationTurnLease lease,
        TimeProvider timeProvider,
        ILogger<ConversationExpiry> logger)
    {
        ArgumentNullException.ThrowIfNull(erasers);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _erasers = erasers.OfType<IPersonalDataEraser>()
            .Where(e => e.ErasureReach == ErasureReach.NamedConversations)
            .ToList();
        _source = sources.OfType<IIdleConversationSource>().FirstOrDefault();
        _lease = lease;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> ExpireIdleAsync(TimeSpan idlePeriod, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(idlePeriod, MinimumIdlePeriod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The conversation itself is deleted through the source, so without one nothing is touched: the stores reached
        // through a conversation would be emptied and the conversation left standing.
        if (_source is null)
        {
            _logger.LogWarning(
                "Conversation retention is enabled, but the conversation store cannot list idle conversations; no conversation will expire.");
            return 0;
        }

        var cutoff = _timeProvider.GetUtcNow() - idlePeriod;
        var expired = 0;
        foreach (var conversation in await _source.ListIdleAsync(cutoff, limit, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                if (await TryExpireAsync(conversation, cutoff, cancellationToken))
                    expired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One conversation failing must not end the sweep for the rest.
                _logger.LogError(ex, "Expiring conversation {ConversationId} failed; it is kept and retried next sweep.", conversation.Id);
            }
        }

        await ScrubAsync();
        return expired;
    }

    /// <summary>Deletes one conversation under its turn lease. Returns whether it was deleted.</summary>
    private async Task<bool> TryExpireAsync(IdleConversation conversation, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var lease = await TryLeaseAsync(conversation.Id, cancellationToken);
        if (lease is null)
            return false;

        await using (lease)
        {
            // Not cancellable from here on: a conversation half-deleted would leave records nothing leads to.
            if (!await _source!.IsIdleAsync(conversation.Id, cutoff, CancellationToken.None) || lease.LeaseLost.IsCancellationRequested)
                return false;

            var subject = new ErasureSubject(conversation.OwnerId, [conversation.Id]);
            if (!await RunStageAsync(ErasureStage.BeforeConversations, subject) || lease.LeaseLost.IsCancellationRequested)
                return false;

            // The delete itself re-checks the owner and the idle time in one step, so a conversation deleted and started
            // again under the same id since the check above is not the one removed. Nothing deleted means the stage after
            // must not run: those records may belong to a conversation still in use.
            if (!await DeleteConversationAsync(conversation, cutoff))
                return false;

            if (!await RunStageAsync(ErasureStage.AfterConversations, subject))
            {
                _logger.LogError(
                    "Expired conversation {ConversationId} was deleted, but a store that is cleared after it failed; its records there have nothing left that leads to them.",
                    conversation.Id);
            }

            return true;
        }
    }

    /// <summary>
    /// Takes the conversation's turn lease, or returns <see langword="null"/> when a turn holds it past the wait or the
    /// conversation has already gone.
    /// </summary>
    private async Task<IConversationTurnLeaseHandle?> TryLeaseAsync(string conversationId, CancellationToken cancellationToken)
    {
        using var waited = new CancellationTokenSource(LeaseWait, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(waited.Token, cancellationToken);
        try
        {
            return await _lease.AcquireAsync(conversationId, linked.Token);
        }
        catch (OperationCanceledException) when (waited.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Conversation {ConversationId} is idle but a turn holds it; it will be expired later.", conversationId);
            return null;
        }
        catch (InvalidOperationException)
        {
            // The durable lease refuses a conversation that no longer exists: someone deleted it first.
            return null;
        }
    }

    /// <summary>Deletes the conversation if it is still the owner's and still idle. Returns whether it was deleted.</summary>
    private async Task<bool> DeleteConversationAsync(IdleConversation conversation, DateTimeOffset cutoff)
    {
        try
        {
            return await _source!.DeleteIfIdleAsync(conversation.Id, conversation.OwnerId, cutoff, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expiring conversation {ConversationId} failed in store {Store}.", conversation.Id, "conversations");
            return false;
        }
    }

    /// <summary>Runs every conversation-scoped store in one stage. Returns whether all of them succeeded.</summary>
    private async Task<bool> RunStageAsync(ErasureStage stage, ErasureSubject subject)
    {
        foreach (var eraser in _erasers.Where(e => e.ErasureStage == stage))
        {
            try
            {
                await eraser.EraseSubjectAsync(subject, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Expiring conversation {ConversationId} failed in store {Store}.",
                    subject.ConversationIds[0], eraser.ErasureStoreName);
                return false;
            }
        }

        return true;
    }

    /// <summary>Clears the deleted bytes from every store's files. A failure is logged and retried by the next sweep.</summary>
    private async Task ScrubAsync()
    {
        foreach (var scrubber in _erasers.OfType<IPersonalDataScrubber>())
        {
            try
            {
                await scrubber.ScrubAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Clearing deleted bytes after conversation expiry failed in store {Store}; the next sweep retries.",
                    ((IPersonalDataEraser)scrubber).ErasureStoreName);
            }
        }
    }
}
