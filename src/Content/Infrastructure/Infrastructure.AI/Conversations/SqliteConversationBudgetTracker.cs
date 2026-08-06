using Application.AI.Common.Interfaces.AI;
using Domain.AI.Budget;
using Domain.Common.Config;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Durable <see cref="IConversationBudgetTracker"/> that keeps each budget key's running total in the
/// conversation database, so a ceiling meant to span a whole conversation also spans the processes
/// running its turns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why durable.</strong> The in-process sibling holds the total in one host's memory. When
/// AgentHub and the Execution API both run turns of one conversation, each enforces a private copy of the
/// same ceiling and the conversation spends roughly twice it. That is not a tuning problem — it is a
/// governance control reporting a number that is not the one configured.
/// </para>
/// <para>
/// <strong>No cache in front of the gate.</strong> Every <see cref="GetStatusAsync"/> reads the row. An
/// in-process cache would restore exactly the divergence this class exists to remove, so the cost of one
/// primary-key lookup per turn is deliberate. It is a keyed read against a table with one row per
/// conversation, taken once between turns, not per token.
/// </para>
/// <para>
/// <strong>Scope.</strong> This reaches as far as the database file does: several processes on one
/// machine, or several sharing one path — the same reach as
/// <see cref="SqliteConversationTurnLease"/>, and the reason the two are registered from one switch.
/// Hosts on different machines with different files each keep their own totals.
/// </para>
/// <para>
/// <strong>On by default, and switchable off.</strong> The ceiling comes from configuration, which
/// defaults to a positive value, so a stock deployment is bounded and does write rows here. When the
/// configured ceiling is ≤ 0 the tracker is instead inert and touches no database at all — a deliberate
/// opt-out rather than the shipped default.
/// </para>
/// <para>
/// <strong>Retention.</strong> Rows are removed only by <see cref="ReleaseAsync"/>, and the two
/// interactive callers never call it because a turn ending is not a conversation ending. Abandoned keys
/// therefore persist, and unlike the in-process sibling — which caps at 50,000 entries and evicts —
/// this one has no bound. At roughly 60 bytes per row, 50,000 abandoned keys cost about 3 MB. Note that
/// this table now grows on <em>every</em> deployment rather than only on ones that opted in, so the
/// retention sweep tracked in issue #253 matters more than it did when the budget shipped disabled.
/// </para>
/// </remarks>
public sealed class SqliteConversationBudgetTracker : IConversationBudgetTracker
{
    private readonly IDbContextFactory<ConversationDbContext> _contextFactory;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteConversationBudgetTracker> _logger;

    /// <summary>Initializes the tracker.</summary>
    /// <param name="contextFactory">Factory for short-lived contexts, one per statement.</param>
    /// <param name="options">
    /// Supplies the ceiling, read live so a configuration reload takes effect — matching the in-process
    /// sibling. Unlike the turn lease's timings, a ceiling changed mid-conversation has an unambiguous
    /// reading: the next gate check compares the accumulated total against the new number.
    /// </param>
    /// <param name="timeProvider">Clock for the retention timestamp.</param>
    /// <param name="logger">Receives the warnings emitted when a budget statement fails.</param>
    /// <param name="schemaInitializer">
    /// Demanded so that resolving this tracker forces the schema to exist before its first statement
    /// runs, the same wiring <see cref="EfCoreConversationStore"/> and
    /// <see cref="SqliteConversationTurnLease"/> use. Nothing is done with the instance — constructing
    /// it is the whole effect. It matters here independently of both, because a host is free to resolve
    /// the tracker first.
    /// </param>
    public SqliteConversationBudgetTracker(
        IDbContextFactory<ConversationDbContext> contextFactory,
        IOptionsMonitor<AppConfig> options,
        TimeProvider timeProvider,
        ILogger<SqliteConversationBudgetTracker> logger,
        SchemaInitializer<ConversationDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(schemaInitializer);

        _contextFactory = contextFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private int ConfiguredBudget => _options.CurrentValue.AI.AgentFramework.ConversationTokenBudget;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Accrual is a single SQLite upsert, which makes "create at this amount" and "add this amount to
    /// what is already there" one atomic statement. Two hosts recording turns at the same instant
    /// therefore sum; the read-modify-write it replaces would have both read zero, both insert, and one
    /// lose its tokens to a primary-key collision.
    /// </para>
    /// <para>
    /// It is written as SQL because EF cannot express <c>ON CONFLICT DO UPDATE</c>, which repeats the
    /// table and column names outside the model —
    /// <c>SqliteConversationBudgetTrackerTests.Accrual_WritesRowsTheEntityModelCanRead</c> exists to fail
    /// if the two ever drift apart. The interpolated form is not string concatenation: EF turns each
    /// hole into a bound parameter.
    /// </para>
    /// </remarks>
    public async Task RecordUsageAsync(
        string budgetKey,
        int tokensUsed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);
        ArgumentOutOfRangeException.ThrowIfNegative(tokensUsed);

        if (tokensUsed == 0 || ConfiguredBudget <= 0)
            return;

        // UtcTicks by hand: the model's value converter applies to the entity, not to a parameter on a
        // raw statement, so passing a DateTimeOffset here would write a value the converter could not
        // read back.
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var tokens = (long)tokensUsed;

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO conversation_budgets (BudgetKey, ConsumedTokens, UpdatedAt)
                VALUES ({budgetKey}, {tokens}, {nowTicks})
                ON CONFLICT(BudgetKey) DO UPDATE SET
                    ConsumedTokens = ConsumedTokens + excluded.ConsumedTokens,
                    UpdatedAt = excluded.UpdatedAt
                """,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The contract is that this never throws, and the turn whose tokens these are has already
            // completed and been paid for — failing it now would cost the caller the work as well as
            // the accounting. The consequence is stated plainly rather than hidden: these tokens are
            // not counted, so the ceiling under-enforces by this turn until the database recovers.
            _logger.LogWarning(
                ex,
                "Conversation budget accrual failed for {BudgetKey}; {Tokens} tokens are not counted "
                    + "against its ceiling",
                budgetKey,
                tokensUsed);
        }
    }

    /// <inheritdoc />
    public async Task<ConversationBudgetStatus> GetStatusAsync(
        string budgetKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);

        var budget = ConfiguredBudget;
        if (budget <= 0)
            return ConversationBudgetStatus.Disabled;

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var consumed = await context.ConversationBudgets
                .Where(e => e.BudgetKey == budgetKey)
                .Select(e => (long?)e.ConsumedTokens)
                .FirstOrDefaultAsync(cancellationToken);

            // Clamped on the way out: the column is 64-bit so a long conversation cannot overflow its
            // running total, while the status the callers compare against is not.
            var projected = (int)Math.Min(int.MaxValue, consumed ?? 0L);
            return new ConversationBudgetStatus(true, budget, projected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fails open, for the same reason accrual does: this gate is consulted between turns to end
            // a conversation gracefully, never to report an error, and a database blip is not a reason
            // to refuse every turn in flight. It is logged at warning because an unenforced ceiling is
            // worth noticing, and the durable store backing the same file is failing too.
            _logger.LogWarning(
                ex,
                "Conversation budget read failed for {BudgetKey}; treating it as within its ceiling",
                budgetKey);

            return new ConversationBudgetStatus(true, budget, 0);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string budgetKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(budgetKey);

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            await context.ConversationBudgets
                .Where(e => e.BudgetKey == budgetKey)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Conversation budget release failed for {BudgetKey}; its row remains", budgetKey);
        }
    }
}
