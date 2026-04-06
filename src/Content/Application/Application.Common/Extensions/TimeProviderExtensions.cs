namespace Application.Common.Extensions;

/// <summary>
/// Extension methods for <see cref="TimeProvider"/> adding harness-specific
/// convenience operations. Standardizes all time access through the framework's
/// built-in abstraction rather than a custom <c>IDateTime</c> interface.
/// </summary>
/// <remarks>
/// Inject <see cref="TimeProvider"/> (singleton) into any service that needs time.
/// Use <c>Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider</c> in tests.
/// </remarks>
public static class TimeProviderExtensions
{
    /// <summary>
    /// Returns the current time adjusted to the specified IANA timezone.
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="ianaTimeZoneId">
    /// IANA timezone identifier (e.g., "America/New_York", "Europe/London", "Asia/Tokyo").
    /// </param>
    /// <returns>The current time in the specified timezone.</returns>
    /// <exception cref="TimeZoneNotFoundException">
    /// Thrown when <paramref name="ianaTimeZoneId"/> is not a valid timezone identifier.
    /// </exception>
    public static DateTimeOffset GetTimeInZone(this TimeProvider timeProvider, string ianaTimeZoneId)
    {
        var utcNow = timeProvider.GetUtcNow();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        return TimeZoneInfo.ConvertTime(utcNow, timeZone);
    }

    /// <summary>
    /// Returns the elapsed time since <paramref name="since"/>.
    /// Convenience for timing agent turns, tool executions, and pipeline stages.
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="since">The start timestamp.</param>
    /// <returns>Elapsed duration from <paramref name="since"/> until now.</returns>
    public static TimeSpan ElapsedSince(this TimeProvider timeProvider, DateTimeOffset since) =>
        timeProvider.GetUtcNow() - since;
}
