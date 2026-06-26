namespace Domain.AI.WorkMemory;

/// <summary>
/// The terminal outcome of a single agent work episode (one conversation turn). Used by the
/// overnight synthesis pass to separate successful trajectories worth reinforcing from failed
/// or corrected ones worth learning from.
/// </summary>
public enum EpisodeOutcome
{
    /// <summary>The turn completed successfully and produced a response.</summary>
    Success = 0,

    /// <summary>The turn failed — the agent did not produce a usable response.</summary>
    Failure = 1
}
