namespace Domain.AI.Planner;

/// <summary>
/// Determines how retry delays increase between attempts.
/// </summary>
public enum BackoffStrategy
{
    /// <summary>Constant delay between retries.</summary>
    Fixed,

    /// <summary>Delay increases linearly with each attempt.</summary>
    Linear,

    /// <summary>Delay doubles with each attempt (recommended for external services).</summary>
    Exponential
}
