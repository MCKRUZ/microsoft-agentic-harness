using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Common.Extensions;

/// <summary>
/// Starts the Azure SDK EventSource-to-<see cref="ILogger"/> bridge at host start.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AzureEventSourceLogForwarder"/> needs an <see cref="ILoggerFactory"/> that only
/// exists once the DI container is built, so registration alone does not start forwarding —
/// something has to resolve it and call <see cref="AzureEventSourceLogForwarder.Start"/>. This
/// hosted service does that, and nothing else. In particular it makes the Azure Identity SDK's
/// own "<c>DefaultAzureCredential credential selected: {0}</c>" message (emitted at
/// <c>EventLevel.Informational</c>) visible through the harness's normal logging pipeline,
/// under the <c>Azure.Identity</c> category — see <see cref="AzureIdentityDiagnosticsExtensions"/>
/// for the category filters that keep this signal from being drowned out by other Azure SDK
/// diagnostic traffic.
/// </para>
/// <para>
/// The forwarder is owned by the DI container (registered as a singleton) and is disposed by
/// the container on shutdown, which detaches the underlying event listener. This service only
/// triggers the start.
/// </para>
/// </remarks>
public sealed class AzureIdentityLogForwarderHostedService : IHostedService
{
    private readonly AzureEventSourceLogForwarder _logForwarder;
    private readonly ILogger<AzureIdentityLogForwarderHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureIdentityLogForwarderHostedService"/> class.
    /// </summary>
    /// <param name="logForwarder">The Azure SDK EventSource-to-<see cref="ILogger"/> bridge to start.</param>
    /// <param name="logger">Logger for startup diagnostics.</param>
    public AzureIdentityLogForwarderHostedService(
        AzureEventSourceLogForwarder logForwarder,
        ILogger<AzureIdentityLogForwarderHostedService> logger)
    {
        _logForwarder = logForwarder;
        _logger = logger;
    }

    /// <summary>
    /// Starts forwarding Azure SDK EventSource messages to <see cref="ILogger"/>.
    /// </summary>
    /// <param name="cancellationToken">Token to observe while starting.</param>
    /// <returns>A completed task — starting the listener is synchronous.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logForwarder.Start();

        _logger.LogInformation(
            "Azure SDK diagnostics forwarder started — DefaultAzureCredential's selected " +
            "credential now logs under the Azure.Identity category.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
