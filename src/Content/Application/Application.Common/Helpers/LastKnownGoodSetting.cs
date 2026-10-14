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
/// <b>Primed at construction.</b> The constructor reads the setting once, so "last good" is the value the
/// host actually started with and not the shipped default. That matters when the fallback is acted on: a
/// retention window of 365 days must not silently become 90 because the file was edited badly before the
/// first tick. The seed is used only if that first read also fails. Pass the shipped
/// default (for example <c>new LearningsConfig().PruneIntervalHours</c>) rather than a literal, and choose
/// the safe direction for a flag: a sweep that deletes should seed to "disabled". A caller whose read
/// depends on state that is not yet set up at construction must create the setting later, not in its
/// constructor.
/// </para>
/// <para>
/// <b>Logging.</b> The first failed read of a streak is logged at error level; further failures while it
/// lasts are logged at debug, so a loop with a short tick does not flood the log; the recovery is logged
/// at information level. <b>Cancellation is not a configuration failure</b> and is never absorbed.
/// </para>
/// </remarks>
public sealed class LastKnownGoodSetting<T>
{
    private readonly Func<T> _read;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly Lock _gate = new();
    private T _lastGood;
    private bool _failing;

    /// <summary>Initializes a new <see cref="LastKnownGoodSetting{T}"/> and primes it with one read.</summary>
    /// <param name="read">Reads the live value; typically a lambda over <c>IOptionsMonitor.CurrentValue</c>.</param>
    /// <param name="seed">The value to use until a read first succeeds.</param>
    /// <param name="logger">Receives the failure and recovery messages.</param>
    /// <param name="name">A short description of the setting, for the log messages.</param>
    public LastKnownGoodSetting(Func<T> read, T seed, ILogger logger, string name)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _read = read;
        _lastGood = seed;
        _logger = logger;
        _name = name;

        _ = Current;
    }

    /// <summary>
    /// The current value, or the last good one (or the seed) when the live read throws.
    /// </summary>
    public T Current
    {
        get
        {
            T value;
            try
            {
                value = _read();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FallBack(ex);
            }

            Remember(value);
            return value;
        }
    }

    private void Remember(T value)
    {
        bool recovered;
        lock (_gate)
        {
            _lastGood = value;
            recovered = _failing;
            _failing = false;
        }

        if (recovered)
            _logger.LogInformation("Configuration for {Setting} is readable again: {Value}.", _name, value);
    }

    private T FallBack(Exception ex)
    {
        T fallback;
        bool firstOfStreak;
        lock (_gate)
        {
            fallback = _lastGood;
            firstOfStreak = !_failing;
            _failing = true;
        }

        if (firstOfStreak)
        {
            _logger.LogError(
                ex,
                "Configuration for {Setting} could not be read; continuing with the last known good value {Value}.",
                _name,
                fallback);
        }
        else
        {
            _logger.LogDebug(ex, "Configuration for {Setting} is still unreadable; using {Value}.", _name, fallback);
        }

        return fallback;
    }
}
