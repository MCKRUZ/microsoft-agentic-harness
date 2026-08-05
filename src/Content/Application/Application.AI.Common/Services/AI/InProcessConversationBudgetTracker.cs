using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.AI;
using Domain.AI.Budget;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.AI;

/// <summary>
/// Thread-safe singleton that accumulates per-key token usage across turns <em>within one process</em> and
/// reports whether the key has exhausted its lifetime budget. Seeds each ceiling from
/// <c>AppConfig.AI.AgentFramework.ConversationTokenBudget</c>, read live so a config reload takes effect.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-host only.</strong> The total lives in this process's memory, so two hosts running turns
/// of the same conversation each enforce a private copy of one ceiling and the conversation can spend
/// roughly twice it. Deployments that span hosts should use the durable implementation instead — the
/// registration follows <c>AppConfig.AI.Conversations.Provider</c>, the same switch that chooses the
/// conversation store and turn lease.
/// </para>
/// <para>
/// <strong>Opt-in.</strong> When the configured ceiling is ≤ 0 the tracker is inert:
/// <see cref="GetStatusAsync"/> returns <see cref="ConversationBudgetStatus.Disabled"/> and
/// <see cref="RecordUsageAsync"/> stores nothing, so the default deployment does no per-call dictionary
/// work and conversations run unbounded across turns.
/// </para>
/// <para>
/// <strong>Bounded memory.</strong> A long-lived interactive host can see many conversations and there is
/// no universal "conversation ended" signal, so entries are capped at <see cref="MaxTrackedConversations"/>
/// and the least-recently-touched entries are evicted when the cap is exceeded. Eviction can let an
/// evicted-then-resumed conversation's running total reset (under-enforcing its budget) — a bounded,
/// documented trade-off preferred over unbounded growth. Callers that own a key's whole lifetime should
/// call <see cref="ReleaseAsync"/> on completion.
/// </para>
/// <para>
/// Every member completes synchronously; the asynchronous signatures exist because the durable sibling
/// cannot, and both are reached through <see cref="IConversationBudgetTracker"/>.
/// </para>
/// </remarks>
public sealed class InProcessConversationBudgetTracker : IConversationBudgetTracker
{
    /// <summary>Maximum number of keys tracked before least-recently-used eviction kicks in.</summary>
    internal const int MaxTrackedConversations = 50_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _evictionLock = new();
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InProcessConversationBudgetTracker> _logger;

    /// <summary>Creates the tracker.</summary>
    /// <param name="options">Supplies the live conversation token ceiling.</param>
    /// <param name="timeProvider">Supplies the access timestamps that drive LRU eviction.</param>
    /// <param name="logger">Receives eviction warnings.</param>
    public InProcessConversationBudgetTracker(
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<InProcessConversationBudgetTracker> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private int ConfiguredBudget => _options.CurrentValue.AI.AgentFramework.ConversationTokenBudget;

    /// <inheritdoc />
    public Task RecordUsageAsync(string budgetKey, int tokensUsed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);
        ArgumentOutOfRangeException.ThrowIfNegative(tokensUsed);

        // Disabled or no-op: store nothing so the default (unbounded) deployment never allocates.
        if (tokensUsed == 0 || ConfiguredBudget <= 0)
            return Task.CompletedTask;

        var now = _timeProvider.GetUtcNow().UtcTicks;
        // Stamp the access time at creation so a brand-new entry is never rank-0 (oldest) for a
        // concurrent eviction running before this thread updates the timestamp.
        var entry = _entries.GetOrAdd(budgetKey, _ => new Entry { LastAccessTicks = now });
        entry.Add(tokensUsed);
        entry.LastAccessTicks = now;

        EvictIfOverCapacity();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ConversationBudgetStatus> GetStatusAsync(string budgetKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);

        var budget = ConfiguredBudget;
        if (budget <= 0)
            return Task.FromResult(ConversationBudgetStatus.Disabled);

        if (!_entries.TryGetValue(budgetKey, out var entry))
            return Task.FromResult(new ConversationBudgetStatus(true, budget, 0));

        entry.LastAccessTicks = _timeProvider.GetUtcNow().UtcTicks;
        return Task.FromResult(new ConversationBudgetStatus(true, budget, entry.Consumed));
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string budgetKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);
        _entries.TryRemove(budgetKey, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// When the entry count exceeds the cap, evicts the least-recently-touched entries back down to ~90%
    /// of the cap in a single guarded pass, so concurrent writers don't each scan. Eviction is rare
    /// (only at cap) and abandoned conversations are exactly the ones it reclaims.
    /// </summary>
    private void EvictIfOverCapacity()
    {
        if (_entries.Count <= MaxTrackedConversations)
            return;

        lock (_evictionLock)
        {
            if (_entries.Count <= MaxTrackedConversations)
                return;

            var target = (int)(MaxTrackedConversations * 0.9);
            var toRemove = _entries.Count - target;

            // Snapshot keys ordered by oldest access and drop the oldest `toRemove`.
            var oldest = _entries
                .OrderBy(kvp => kvp.Value.LastAccessTicks)
                .Take(toRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldest)
                _entries.TryRemove(key, out _);

            _logger.LogWarning(
                "Conversation budget tracker evicted {Count} least-recently-used entries (cap {Cap})",
                oldest.Count, MaxTrackedConversations);
        }
    }

    /// <summary>Per-key running total. <see cref="LastAccessTicks"/> drives LRU eviction.</summary>
    private sealed class Entry
    {
        private long _consumed;
        private long _lastAccessTicks;

        /// <summary>
        /// Last access time in UTC ticks. Read/written via <see cref="Volatile"/> so the unlocked
        /// updates in <c>RecordUsageAsync</c>/<c>GetStatusAsync</c> and the read during eviction's
        /// ordering are not torn on a 32-bit runtime (a 64-bit long write is not otherwise guaranteed
        /// atomic).
        /// </summary>
        public long LastAccessTicks
        {
            get => Volatile.Read(ref _lastAccessTicks);
            set => Volatile.Write(ref _lastAccessTicks, value);
        }

        /// <summary>Running total, clamped to <see cref="int.MaxValue"/> for the status projection.</summary>
        public int Consumed => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _consumed));

        public void Add(int tokens) => Interlocked.Add(ref _consumed, tokens);
    }
}
