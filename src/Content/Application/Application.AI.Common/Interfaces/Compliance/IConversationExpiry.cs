namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Deletes conversations nobody has used for a configured period, with everything that belongs to them (#901).
/// </summary>
public interface IConversationExpiry
{
    /// <summary>
    /// Expires up to <paramref name="limit"/> conversations last written to more than <paramref name="idlePeriod"/> ago.
    /// </summary>
    /// <param name="idlePeriod">How long a conversation must go unused; at least one day.</param>
    /// <param name="limit">The most conversations to expire in this call.</param>
    /// <param name="cancellationToken">Stops between conversations; one already being deleted is finished.</param>
    /// <returns>How many conversations were deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="idlePeriod"/> is under a day or <paramref name="limit"/> is not positive. A mistyped period must
    /// never sweep away conversations in use.
    /// </exception>
    Task<int> ExpireIdleAsync(TimeSpan idlePeriod, int limit, CancellationToken cancellationToken);
}
