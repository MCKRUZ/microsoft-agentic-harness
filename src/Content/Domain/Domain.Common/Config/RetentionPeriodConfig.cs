namespace Domain.Common.Config;

/// <summary>
/// How long a store keeps a record before a background sweep deletes it (#901). Shared by the stores whose only rule is
/// age, so each one is configured the same way.
/// </summary>
/// <remarks>
/// Data protection law (GDPR Art. 5(1)(e), storage limitation) does not allow personal data to be kept with no end. Each
/// store that uses this sets its own default for <see cref="RetainFor"/>, sized to what the records are for, and says
/// why in its own documentation.
/// </remarks>
public sealed class RetentionPeriodConfig
{
    /// <summary>The shortest period accepted. Anything shorter is a configuration mistake, not a policy.</summary>
    public static readonly TimeSpan MinimumRetainFor = TimeSpan.FromDays(1);

    /// <summary>Whether the sweep runs. Defaults to <see langword="true"/>.</summary>
    /// <remarks>Switching it off keeps every record until something else deletes it; that is a choice, not a default.</remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a record is kept. At least one day. Defaults to 90 days unless the owning setting says otherwise.</summary>
    public TimeSpan RetainFor { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the sweep runs. Defaults to six hours.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);
}
