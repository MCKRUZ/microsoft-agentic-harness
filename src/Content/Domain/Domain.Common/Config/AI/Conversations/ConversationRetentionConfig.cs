namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Governs the sweep that deletes conversations nobody has used for a long time, with everything that belongs to them
/// (#901). Bound from <c>AppConfig:AI:Conversations:Retention</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it exists.</strong> Data protection law (GDPR Art. 5(1)(e), storage limitation) does not allow personal
/// data to be kept with no end. A transcript holds whatever the person typed and whatever their tools returned, and before
/// this nothing ever removed one the person did not delete themselves.
/// </para>
/// <para>
/// <strong>What "idle" means.</strong> No message written for <see cref="IdlePeriod"/>. Opening a conversation to read it
/// does not reset the clock; continuing it does. A conversation with a turn running is never touched.
/// </para>
/// <para>
/// <strong>What goes with it.</strong> Every record found through the conversation: messages, observability sessions,
/// spilled tool results, prompt-usage rows, its token budget and its call-once ledger rows. Owner-wide data (documents the
/// person ingested, their plans and schedules, their knowledge-graph memories) is not touched; each of those has its own
/// retention.
/// </para>
/// </remarks>
public sealed class ConversationRetentionConfig
{
    /// <summary>Whether the sweep runs. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// On by default so a stock deployment does not keep conversations forever. Switching it off keeps every conversation
    /// until its owner deletes it or asks to be erased; that is a supported choice for a deployment with its own retention
    /// process, but it is a choice, and the deployment's privacy notice should say so.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a conversation must go without a new message before it is deleted. Defaults to 365 days.</summary>
    /// <remarks>
    /// Long on purpose: people return to old conversations, and losing one they expected to find is a visible failure.
    /// Shorten it to match the deployment's privacy notice. At least one day; a shorter value is refused at startup and,
    /// if reloaded while running, by the sweep itself.
    /// </remarks>
    public TimeSpan IdlePeriod { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How often the sweep runs. Defaults to six hours.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The most conversations one sweep deletes. Defaults to 500.</summary>
    /// <remarks>
    /// Bounds one sweep's work so a first run against years of history does not hold the database for long; the rest
    /// go on later sweeps.
    /// </remarks>
    public int BatchSize { get; set; } = 500;
}
