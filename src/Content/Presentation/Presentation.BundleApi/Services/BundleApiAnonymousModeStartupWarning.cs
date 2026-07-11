using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Presentation.BundleApi.Services;

/// <summary>
/// Emits a prominent warning at startup while the bundle API is serving without authentication (the explicit
/// <c>AppConfig:AI:BundleExecution:Auth:AllowAnonymous</c> opt-in). Makes the open-door state impossible to
/// miss in logs, so an anonymous deployment is a conscious, visible choice rather than a silent oversight.
/// </summary>
internal sealed class BundleApiAnonymousModeStartupWarning : IHostedService
{
    private readonly ILogger<BundleApiAnonymousModeStartupWarning> _logger;

    /// <summary>Initializes a new <see cref="BundleApiAnonymousModeStartupWarning"/>.</summary>
    public BundleApiAnonymousModeStartupWarning(ILogger<BundleApiAnonymousModeStartupWarning> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Bundle API is running WITHOUT authentication (AppConfig:AI:BundleExecution:Auth:AllowAnonymous=true). " +
            "Every caller is anonymous and the per-caller capability envelope resolves from an unauthenticated " +
            "principal. This is intended for local development only — never enable it in a shared or production host.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
