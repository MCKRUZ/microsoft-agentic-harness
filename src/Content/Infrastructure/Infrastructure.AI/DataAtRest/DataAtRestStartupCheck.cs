using Domain.Common.Config;
using Domain.Common.Config.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.DataAtRest;

/// <summary>
/// Says at startup, outside Development, that the host keeps personal data in local files it does not encrypt, unless
/// the deployer has recorded how those files are protected (<c>AppConfig:Infrastructure:DataAtRest:Encryption</c>) (#904).
/// </summary>
/// <remarks>
/// <para>
/// The harness cannot tell whether a volume is encrypted, so it does not try: the setting is the deployer's statement,
/// and the check only makes sure the question was answered. A warning rather than a refusal, because encrypting the disk
/// is a deployment's decision and a host on an encrypted volume is not unsafe for lacking the setting.
/// </para>
/// <para>
/// The message names examples rather than a list of stores: a list kept here would drift from the stores the harness
/// actually has. The security guide's data-protection chapter has the options for each store.
/// </para>
/// </remarks>
public sealed class DataAtRestStartupCheck : IHostedService
{
    private readonly IOptions<AppConfig> _config;
    private readonly ILogger<DataAtRestStartupCheck> _logger;
    private readonly IHostEnvironment? _environment;

    /// <summary>Initializes a new instance of the <see cref="DataAtRestStartupCheck"/> class.</summary>
    /// <param name="config">The application configuration holding the deployer's statement.</param>
    /// <param name="logger">Logger for the warning.</param>
    /// <param name="environment">
    /// The environment the host runs in. A composition with no host environment is treated as not Development, so it
    /// warns, as the content-safety and egress startup checks do.
    /// </param>
    public DataAtRestStartupCheck(
        IOptions<AppConfig> config, ILogger<DataAtRestStartupCheck> logger, IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var encryption = _config.Value.Infrastructure.DataAtRest.Encryption;
        if (encryption == DataAtRestEncryption.Volume)
            _logger.LogInformation(
                "Data at rest: the deployer states the volume holding this host's local data is encrypted " +
                "(AppConfig:Infrastructure:DataAtRest:Encryption is Volume).");
        else if (!(_environment?.IsDevelopment() ?? false))
            _logger.LogWarning(
                "This host keeps personal data in local files it does not encrypt, such as its SQLite databases, audit " +
                "trails and logs. Encrypt the volume they are on, then set AppConfig:Infrastructure:DataAtRest:Encryption " +
                "to Volume. The security guide's data-protection chapter lists the options for each store.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
