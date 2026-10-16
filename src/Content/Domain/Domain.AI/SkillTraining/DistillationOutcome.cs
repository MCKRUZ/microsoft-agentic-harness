namespace Domain.AI.SkillTraining;

/// <summary>
/// The result of distilling an epoch: either a lesson to keep or a stated reason there was none (#845).
/// </summary>
/// <remarks>
/// Two distinct types rather than one with an optional entry, so a caller cannot read "no lesson" as an
/// entry with empty fields, and cannot forget to look. A class hierarchy with the constructors closed, not
/// records: the hierarchy is polymorphic.
/// </remarks>
public abstract class DistillationOutcome
{
    private protected DistillationOutcome()
    {
    }

    /// <summary>A lesson was drawn.</summary>
    /// <param name="entry">The lesson.</param>
    public static DistillationOutcome Written(ExperienceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new EntryWritten(entry);
    }

    /// <summary>No lesson was drawn.</summary>
    /// <param name="reason">Why not, in a sentence.</param>
    public static DistillationOutcome None(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NoEntry(reason);
    }
}

/// <summary>A lesson was drawn from the epoch.</summary>
public sealed class EntryWritten : DistillationOutcome
{
    internal EntryWritten(ExperienceEntry entry) => Entry = entry;

    /// <summary>The lesson.</summary>
    public ExperienceEntry Entry { get; }
}

/// <summary>The epoch gave nothing worth keeping.</summary>
public sealed class NoEntry : DistillationOutcome
{
    internal NoEntry(string reason) => Reason = reason;

    /// <summary>Why no lesson was drawn.</summary>
    public string Reason { get; }
}
