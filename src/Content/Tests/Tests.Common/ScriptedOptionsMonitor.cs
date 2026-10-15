using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Tests.Common;

/// <summary>
/// An <see cref="IOptionsMonitor{TOptions}"/> that throws the validation failure on chosen reads, counted
/// from 1, and serves the value on every other read. A seed value only shows when a setting's live read
/// fails <i>and</i> no read has ever succeeded for it, which a "start throwing and stay thrown" monitor
/// cannot arrange for one setting while the others stay readable. The read numbers a test passes in
/// follow the order the service under test reads its settings, so each test names that order beside them.
/// </summary>
public sealed class ScriptedOptionsMonitor(AppConfig value, params int[] failOnReads) : IOptionsMonitor<AppConfig>
{
    private int _reads;

    public AppConfig CurrentValue =>
        failOnReads.Contains(Interlocked.Increment(ref _reads))
            ? throw new OptionsValidationException("AppConfig", typeof(AppConfig), ["a mistyped live edit"])
            : value;

    public AppConfig Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
}
