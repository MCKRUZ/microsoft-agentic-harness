using System.Globalization;
using Tests.AI.Fakes;

namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// The load target's command line, parsed and validated once at start-up.
/// </summary>
/// <param name="Port">Loopback port Kestrel listens on.</param>
/// <param name="Model">How the simulated model behaves.</param>
/// <param name="Callers">Distinct caller identities to mint tokens for; each is its own rate-limit partition.</param>
/// <param name="Tenants">Tenants the callers are spread across, round-robin.</param>
/// <param name="OutputDirectory">Where the fixtures k6 reads (tokens, bundle archive, target description) are written.</param>
/// <param name="TokenLifetime">How long minted tokens stay valid; must outlast the longest run.</param>
/// <param name="EnableToolInvocation">Turns on direct tool invocation and grants every caller the demo tool it needs.</param>
/// <param name="WithTelemetry">Keeps OTLP export on, so a run's telemetry reaches the configured collector.</param>
public sealed record LoadTargetOptions(
    int Port,
    SimulatedModelProfile Model,
    int Callers,
    int Tenants,
    string OutputDirectory,
    TimeSpan TokenLifetime,
    bool EnableToolInvocation,
    bool WithTelemetry)
{
    /// <summary>The usage text printed when the command line is wrong.</summary>
    public const string Usage =
        "Usage: Presentation.ExecutionApi.LoadTarget --out <dir> [--port 5080] [--model instant|realistic|slow|flaky] " +
        "[--failure-rate 0..1] [--callers 200] [--tenants 4] [--token-lifetime-minutes 240] " +
        "[--enable-tool-invocation] [--with-telemetry]";

    /// <summary>
    /// Parses <paramref name="args"/>. Returns the options, or an error message naming the first bad argument.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    public static (LoadTargetOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (Flags.Contains(name))
            {
                flags.Add(name);
                continue;
            }

            // A misspelled option must fail here: accepted silently, its default would surface much later
            // as a confusing failure (too few callers for a stress ramp, say).
            if (!ValueOptions.Contains(name))
                return (null, $"Unrecognised argument '{name}'.");
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return (null, $"{name} needs a value.");
            values[name] = args[++i];
        }

        return Build(values, flags);
    }

    private static readonly HashSet<string> Flags =
        new(["--enable-tool-invocation", "--with-telemetry"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ValueOptions = new(
        ["--out", "--port", "--model", "--failure-rate", "--callers", "--tenants", "--token-lifetime-minutes"],
        StringComparer.OrdinalIgnoreCase);

    private static (LoadTargetOptions? Options, string? Error) Build(
        IReadOnlyDictionary<string, string> values, IReadOnlySet<string> flags)
    {
        if (!values.TryGetValue("--out", out var output) || string.IsNullOrWhiteSpace(output))
            return (null, "--out is required.");

        var modelName = values.GetValueOrDefault("--model", "realistic");
        if (!SimulatedModelProfile.TryGetNamed(modelName, out var model))
            return (null, $"Unknown --model '{modelName}'.");

        if (values.TryGetValue("--failure-rate", out var rateText))
        {
            // Written as "not within", so NaN (which parses, and fails every comparison) is rejected too.
            if (!double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || !(rate is >= 0 and <= 1))
                return (null, "--failure-rate must be a number from 0 to 1.");
            model = model!.WithTransientFailureRate(rate);
        }

        var port = ReadInt(values, "--port", 5080, 1, 65535);
        var callers = ReadInt(values, "--callers", 200, 1, 100_000);
        var tenants = ReadInt(values, "--tenants", 4, 1, 1_000);
        var lifetime = ReadInt(values, "--token-lifetime-minutes", 240, 5, 7 * 24 * 60);
        var bad = new[] { port, callers, tenants, lifetime }.FirstOrDefault(r => r.Error is not null).Error;
        if (bad is not null)
            return (null, bad);

        return (new LoadTargetOptions(
            port.Value, model!, callers.Value, tenants.Value, Path.GetFullPath(output),
            TimeSpan.FromMinutes(lifetime.Value),
            flags.Contains("--enable-tool-invocation"), flags.Contains("--with-telemetry")), null);
    }

    private static (int Value, string? Error) ReadInt(
        IReadOnlyDictionary<string, string> values, string name, int fallback, int min, int max)
    {
        if (!values.TryGetValue(name, out var text))
            return (fallback, null);

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? (value, null)
            : (0, $"{name} must be a whole number from {min} to {max}.");
    }
}
