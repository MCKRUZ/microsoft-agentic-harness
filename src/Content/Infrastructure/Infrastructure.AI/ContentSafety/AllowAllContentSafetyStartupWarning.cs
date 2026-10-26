using Domain.Common.Config;
using Domain.Common.Config.AI.ContentSafety;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.ContentSafety;

/// <summary>
/// Says at startup, outside the Development environment, that request content is not being screened (#904).
/// </summary>
/// <remarks>
/// The allow-all provider is the default so a host runs without an Azure resource. That is a reasonable start and a poor
/// place to stay unnoticed: the pipeline, its metrics and its safety events all look like screening is happening. A
/// warning rather than a refusal, because screening is a deployment's choice; running without it is not unsafe in itself
/// the way serving without authentication is.
/// </remarks>
public sealed class AllowAllContentSafetyStartupWarning : IHostedService
{
    private readonly IOptions<AppConfig> _config;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<AllowAllContentSafetyStartupWarning> _logger;

    /// <summary>Initializes a new instance of the <see cref="AllowAllContentSafetyStartupWarning"/> class.</summary>
    /// <param name="config">Application configuration holding the content-safety provider.</param>
    /// <param name="logger">Logger for the warning.</param>
    /// <param name="environment">
    /// The environment the host runs in. A composition with no host environment is treated as not Development, so it
    /// warns: the safe direction, matching <see cref="Egress.EgressStartupValidator"/>.
    /// </param>
    public AllowAllContentSafetyStartupWarning(
        IOptions<AppConfig> config,
        ILogger<AllowAllContentSafetyStartupWarning> logger,
        IHostEnvironment? environment = null)
    {
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Value.AI.ContentSafety.Provider == ContentSafetyProvider.AllowAll && !(_environment?.IsDevelopment() ?? false))
            _logger.LogWarning(
                "Content safety is not screening anything: AppConfig:AI:ContentSafety:Provider is AllowAll, so every " +
                "request passes. Set it to AzureAIContentSafety with an Endpoint to screen request text.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
