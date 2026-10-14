using Microsoft.Extensions.Logging;

namespace Application.Common.Helpers;

/// <summary>
/// Reads one live configuration value for a long-running background loop, and keeps the loop alive when
/// that read fails by falling back to the last value that read successfully.
/// </summary>
/// <typeparam name="T">The type of the setting.</typeparam>
/// <remarks>
/// <para>
/// <b>Why a background loop needs this.</b> A configuration section bound with
/// <c>ValidateFluentValidation</c> makes <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> <em>throw</em> after a
/// hot reload that fails validation, and keep throwing until the file is fixed. A
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that reads <c>CurrentValue</c> outside its
/// per-tick <c>try</c> lets that exception escape <c>ExecuteAsync</c>, and the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> then stops the whole host: a mistyped live edit
/// becomes an outage. Containing the read alone is not enough, because an interval is needed <em>before</em>
/// the wait it controls, so a failed read has to produce a value; the last good one is the least surprising.
/// </para>
/// <para>
/// <b>Seed.</b> Used only when the setting has never been read successfully. Pass the shipped default
/// (for example <c>new LearningsConfig().PruneIntervalHours</c>) rather than a literal, so it cannot drift
/// from the documented default, and choose the safe direction for a flag: a sweep that deletes should seed
/// to "disabled".
/// </para>
/// <para>
/// <b>Cancellation is not a configuration failure</b> and is never absorbed. Every other exception is,
/// and is logged at error level on each failed read so the bad edit stays visible for as long as it lasts.
/// </para>
/// </remarks>
public sealed class LastKnownGoodSetting<T>
{
    private readonly Func<T> _read;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly Lock _gate = new();
    private T _lastGood;

    /// <summary>Initializes a new <see cref="LastKnownGoodSetting{T}"/>.</summary>
    /// <param name="read">Reads the live value; typically a lambda over <c>IOptionsMonitor.CurrentValue</c>.</param>
    /// <param name="seed">The value to use until a read first succeeds.</param>
    /// <param name="logger">Receives an error for each failed read.</param>
    /// <param name="name">A short description of the setting, for the log message.</param>
    public LastKnownGoodSetting(Func<T> read, T seed, ILogger logger, string name)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _read = read;
        _lastGood = seed;
        _logger = logger;
        _name = name;
    }

    /// <summary>
    /// The current value, or the last good one (or the seed) when the live read throws.
    /// </summary>
    public T Current
    {
        get
        {
            try
            {
                var value = _read();
                lock (_gate)
                {
                    _lastGood = value;
                }

                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                T fallback;
                lock (_gate)
                {
                    fallback = _lastGood;
                }

                _logger.LogError(
                    ex,
                    "Configuration for {Setting} could not be read; continuing with the last known good value {Value}.",
                    _name,
                    fallback);

                return fallback;
            }
        }
    }
}
