namespace Domain.AI.Learnings;

/// <summary>
/// Determines the temporal decay rate for a learning entry.
/// <see cref="Volatile"/> entries expire quickly (default 7 days),
/// <see cref="Stable"/> entries persist longer (default 180 days),
/// and <see cref="Permanent"/> entries never decay.
/// Shelf lives are configurable via <c>LearningsConfig</c>.
/// </summary>
public enum DecayClass
{
    /// <summary>Short-lived knowledge. Decays linearly over VolatileShelfLifeDays.</summary>
    Volatile = 0,
    /// <summary>Long-lived knowledge. Decays linearly over StableShelfLifeDays.</summary>
    Stable = 1,
    /// <summary>Immortal knowledge. Freshness always returns 1.0.</summary>
    Permanent = 2
}
