using Application.AI.Common.Interfaces.Audit;
using Application.Core.Validation;
using Azure.Identity;
using Azure.Storage.Blobs;
using Domain.Common.Config;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Presentation.AuditArchiver;

/// <summary>
/// Entry point for the audit archiver (#416): moves finished audit day-files into the write-once archive and witnesses
/// each log's newest record, as a scheduled job separate from the application.
/// </summary>
/// <remarks>
/// <para><b>Usage:</b></para>
/// <code>
/// audit-archiver init                          # once: mark the archive ready
/// audit-archiver witness                       # every 15-60 minutes: pin each log's newest record
/// audit-archiver run                           # hourly or daily: witness, then archive finished days
/// audit-archiver verify                        # daily: check every log end to end
/// audit-archiver accept &lt;log&gt; &lt;file&gt; &lt;sha256&gt; &lt;reason...&gt;   # let an investigated torn file through
///   [--config &lt;file&gt;] [--timeout-minutes &lt;n&gt;]
/// </code>
/// <para>
/// Reads <c>appsettings.json</c> beside it, then <c>--config</c>, then environment variables (<c>AppConfig__AI__...</c>):
/// the audit log locations, <c>AppConfig:AI:Audit:ArchivePath</c> and <c>AppConfig:AI:Audit:Archiver</c>. Keep that
/// configuration, and this program, where the application cannot change them.
/// </para>
/// <para>
/// Exit codes: 0 every log done; 1 a log stopped (gap, rewrite, changed copy) or the command timed out: page someone;
/// 2 bad command line or configuration; 3 the archive is not ready (not mounted, or init never run).
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Entry point.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArchiverCommand command;
        try
        {
            command = ArchiverCommand.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = loggerFactory.CreateLogger("AuditArchiver");

        var config = LoadConfig(command.ConfigFile);
        if (Validate(config) is { } problem)
        {
            logger.LogError("Configuration is not valid: {Problem}", problem);
            return ExitCodes.Usage;
        }

        using var timeout = new CancellationTokenSource(command.Timeout);
        try
        {
            var archiver = new Infrastructure.AI.Audit.AuditArchiver(
                StoreFor(config), config.AI.Audit.Archiver, TimeProvider.System, loggerFactory.CreateLogger<Infrastructure.AI.Audit.AuditArchiver>());
            return await ArchiverRunner.RunAsync(command, archiver, AuditChains.All(config, TimeProvider.System), logger, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogCritical("The audit archiver gave up after {Timeout}.", command.Timeout);
            return ExitCodes.Stopped;
        }
        catch (Exception ex)
        {
            // Anything else (a store that cannot be reached, credentials refused): still exit 1, so someone is paged.
            logger.LogCritical(ex, "The audit archiver failed.");
            return ExitCodes.Stopped;
        }
    }

    private static AppConfig LoadConfig(string? configFile)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);
        if (configFile is not null)
            builder.AddJsonFile(Path.GetFullPath(configFile), optional: false);

        return builder.AddEnvironmentVariables().Build().GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
    }

    private static string? Validate(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.AI.Audit.ArchivePath) && string.IsNullOrWhiteSpace(config.AI.Audit.Archiver.BlobContainerUri))
            return "set AppConfig:AI:Audit:ArchivePath (a folder) or AppConfig:AI:Audit:Archiver:BlobContainerUri.";

        if (config.AI.Audit.Archiver.MinimumAgeDays < 1)
            return "AppConfig:AI:Audit:Archiver:MinimumAgeDays must be at least 1.";

        var overlap = new AuditArchivePathValidator().Validate(config.AI);
        return overlap.IsValid ? null : string.Join(" ", overlap.Errors.Select(e => e.ErrorMessage));
    }

    private static IAuditArchiveStore StoreFor(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.AI.Audit.Archiver.BlobContainerUri)
            ? new FileAuditArchiveStore(config.AI.Audit.ArchivePath)
            : new BlobAuditArchiveStore(new BlobContainerClient(new Uri(config.AI.Audit.Archiver.BlobContainerUri), new DefaultAzureCredential()));
}
