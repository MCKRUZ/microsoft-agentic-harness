using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Presentation.AuditArchiver;

/// <summary>Runs one archiver command over every configured audit log and turns the outcomes into an exit code.</summary>
internal static class ArchiverRunner
{
    /// <summary>Runs <paramref name="command"/>.</summary>
    /// <param name="command">The parsed command line.</param>
    /// <param name="archiver">The archiver.</param>
    /// <param name="chains">Every configured audit log.</param>
    /// <param name="logger">Logger for the summary.</param>
    /// <param name="cancellationToken">Cancellation token, cancelled when the command times out.</param>
    /// <returns>The exit code (<see cref="ExitCodes"/>).</returns>
    public static async Task<int> RunAsync(
        ArchiverCommand command,
        Infrastructure.AI.Audit.AuditArchiver archiver,
        IReadOnlyList<AuditChainLocation> chains,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (command.Verb == "init")
        {
            var written = await archiver.InitializeAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(written ? "Archive marked ready." : "Archive was already marked ready.");
            return ExitCodes.Done;
        }

        if (command.Verb == "accept")
        {
            var chain = chains.FirstOrDefault(c => c.Name == command.Arguments[0]);
            if (chain is null)
            {
                logger.LogError("No audit log named {Log}; configured: {Logs}.", command.Arguments[0], string.Join(", ", chains.Select(c => c.Name)));
                return ExitCodes.Usage;
            }

            var linesSteppedOver = int.Parse(command.Arguments[3], System.Globalization.CultureInfo.InvariantCulture);
            var reason = string.Join(' ', command.Arguments.Skip(4));
            return ExitCodeOf([await archiver.AcceptAsync(
                chain, command.Arguments[1], command.Arguments[2], linesSteppedOver, reason, cancellationToken).ConfigureAwait(false)]);
        }

        var results = new List<AuditArchiveResult>();
        foreach (var chain in chains)
        {
            results.Add(command.Verb switch
            {
                "witness" => await archiver.WitnessAsync(chain, cancellationToken).ConfigureAwait(false),
                "run" => await archiver.ArchiveAsync(chain, cancellationToken).ConfigureAwait(false),
                _ => await archiver.VerifyAsync(chain, cancellationToken).ConfigureAwait(false),
            });
        }

        foreach (var result in results)
        {
            logger.LogInformation(
                "{Log}: {Status}, {Archived} file(s) archived{Problem}",
                result.Chain, result.Status, result.FilesArchived, result.Problem is null ? string.Empty : $": {result.Problem}");
        }

        return ExitCodeOf(results);
    }

    /// <summary>Stopped wins over not ready, which wins over done: a stop is what must page someone.</summary>
    /// <param name="results">Every log's outcome.</param>
    /// <returns>The exit code.</returns>
    internal static int ExitCodeOf(IReadOnlyList<AuditArchiveResult> results) =>
        results.Any(r => r.Status == AuditArchiveStatus.Stopped) ? ExitCodes.Stopped
        : results.Any(r => r.Status == AuditArchiveStatus.NotReady) ? ExitCodes.NotReady
        : ExitCodes.Done;
}
