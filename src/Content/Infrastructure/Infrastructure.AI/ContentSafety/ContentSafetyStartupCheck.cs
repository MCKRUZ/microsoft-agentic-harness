using Application.AI.Common.Interfaces.Agent;
using Domain.Common.Config;
using Domain.Common.Config.AI.ContentSafety;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.ContentSafety;

/// <summary>
/// Settles content safety at startup (#904): builds the configured screening service, so a configuration it refuses
/// stops the host instead of failing every request, and says so when nothing is being screened outside Development.
/// </summary>
/// <remarks>
/// <para>
/// Taking <see cref="ITextContentSafetyService"/> as a dependency is the point: the host constructs every hosted service
/// as it starts, so the service's factory, and the validation it runs on the configuration the service is built from,
/// run then, in every host. That includes hosts that compose their own options pipeline and so never run the
/// <c>ValidateOnStart</c> check, where a bad configuration would otherwise surface as the first screened request failing,
/// and every one after it.
/// </para>
/// <para>
/// The allow-all provider is the default so a host runs without an Azure resource. That is a reasonable start and a poor
/// place to stay unnoticed: the pipeline, its metrics and its safety events all look like screening is happening. A
/// warning rather than a refusal, because screening is a deployment's choice; running without it is not unsafe in itself
/// the way serving without authentication is.
/// </para>
/// </remarks>
public sealed class ContentSafetyStartupCheck : IHostedService
{
    private readonly IOptions<AppConfig> _config;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<ContentSafetyStartupCheck> _logger;

    /// <summary>Initializes a new instance of the <see cref="ContentSafetyStartupCheck"/> class.</summary>
    /// <param name="config">Application configuration holding the content-safety provider.</param>
    /// <param name="service">The configured screening service, built here so a refused configuration fails startup.</param>
    /// <param name="logger">Logger for the warning.</param>
    /// <param name="environment">
    /// The environment the host runs in. A composition with no host environment is treated as not Development, so it
    /// warns: the safe direction, matching <see cref="Egress.EgressStartupValidator"/>.
    /// </param>
    public ContentSafetyStartupCheck(
        IOptions<AppConfig> config,
        ITextContentSafetyService service,
        ILogger<ContentSafetyStartupCheck> logger,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(service);
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
