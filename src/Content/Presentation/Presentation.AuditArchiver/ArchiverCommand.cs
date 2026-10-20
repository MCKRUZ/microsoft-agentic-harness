using System.Globalization;

namespace Presentation.AuditArchiver;

/// <summary>A parsed command line for the audit archiver.</summary>
/// <param name="Verb">init, witness, run, verify or accept.</param>
/// <param name="Arguments">The verb's own arguments (for accept: log, file, SHA-256, lines stepped over, reason).</param>
/// <param name="Timeout">How long the whole command may take before it gives up and exits 1.</param>
/// <param name="ConfigFile">An extra configuration file to read, or <c>null</c>.</param>
internal sealed record ArchiverCommand(string Verb, IReadOnlyList<string> Arguments, TimeSpan Timeout, string? ConfigFile)
{
    /// <summary>The verbs, in the order the usage text lists them.</summary>
    public static readonly IReadOnlyList<string> Verbs = ["init", "witness", "run", "verify", "accept"];

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentException">The command line is not valid.</exception>
    public static ArchiverCommand Parse(IReadOnlyList<string> args)
    {
        var timeout = TimeSpan.FromMinutes(30);
        string? configFile = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--timeout-minutes":
                    if (i + 1 >= args.Count || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                        throw new ArgumentException("--timeout-minutes needs a whole number of minutes above zero.");
                    timeout = TimeSpan.FromMinutes(minutes);
                    break;
                case "--config":
                    if (i + 1 >= args.Count)
                        throw new ArgumentException("--config needs a file path.");
                    configFile = args[++i];
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count == 0 || !Verbs.Contains(positional[0]))
            throw new ArgumentException($"Name one command: {string.Join(", ", Verbs)}.");

        var verb = positional[0];
        var rest = positional.Skip(1).ToList();
        if (verb == "accept"
                ? rest.Count < 5 || !int.TryParse(rest[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                : rest.Count > 0)
        {
            throw new ArgumentException(verb == "accept"
                ? "accept needs: <log> <file> <sha256> <lines-stepped-over> <reason...>"
                : $"{verb} takes no arguments.");
        }

        return new ArchiverCommand(verb, rest, timeout, configFile);
    }
}
