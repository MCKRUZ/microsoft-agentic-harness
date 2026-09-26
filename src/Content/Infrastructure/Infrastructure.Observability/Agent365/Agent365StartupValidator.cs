using System.Reflection;
using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Observability.Agent365;

/// <summary>
/// One-shot startup validator for Agent 365 export. Refuses to boot a host that has enabled agent
/// activity export in a place where the exporter cannot actually be wired, and otherwise records what
/// was enabled.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the alternative is silence. The exporter is attached to the OpenTelemetry
/// builder that only the web-telemetry host path creates; a console or worker host builds its tracer
/// provider standalone and never reaches that call. Enabling the feature on such a host therefore
/// publishes turn attribution, costs the overhead, and exports nothing — with no error anywhere. The
/// agent simply never appears in the tenant's inventory, which is indistinguishable from a licensing
/// or consent problem and sends whoever debugs it to the wrong place entirely.
/// </para>
/// <para>
/// Whether a host takes the web path is decided by <c>Observability:WebTelemetryProjects</c>, matched
/// against the entry assembly name — so that is what this checks, rather than probing the container
/// for vendor services that are internal to their assembly and cannot be resolved by type here.
/// </para>
/// <para>
/// Config <em>values</em> are not re-checked here; <c>Agent365ExporterConfigValidator</c> already
/// fails the host on start for a malformed agent or tenant id. This validator covers the one thing a
/// single-section validator structurally cannot see: whether this host shape can export at all.
/// </para>
/// </remarks>
public sealed class Agent365StartupValidator : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<Agent365StartupValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="Agent365StartupValidator"/> class.</summary>
    public Agent365StartupValidator(
        IOptionsMonitor<AppConfig> config,
        ILogger<Agent365StartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var observability = _config.CurrentValue.Observability;
        var config = observability.Exporters.Agent365;

        if (!config.Enabled)
        {
            return Task.CompletedTask;
        }

        var entryAssembly = Assembly.GetEntryAssembly()?.GetName().Name ?? "UnknownService";

        // The shared rule on ObservabilityConfig, not a second copy of it. This validator's whole
        // premise is that its answer equals the one the telemetry composition takes; two independently
        // maintained expressions would make that a coincidence, and a drifted copy would restore the
        // silent failure this exists to prevent.
        if (!observability.IsWebTelemetryHost(entryAssembly))
        {
            throw new InvalidOperationException(
                $"Agent 365 export is enabled but this host ('{entryAssembly}') is not listed in "
                + "AppConfig:Observability:WebTelemetryProjects. The Agent 365 exporter attaches to the "
                + "OpenTelemetry builder that only the web-telemetry path creates, so on this host it "
                + "would never be wired and no agent activity would reach the tenant's control plane — "
                + $"silently. Add '{entryAssembly}' to Observability:WebTelemetryProjects, or set "
                + "Observability:Exporters:Agent365:Enabled to false for this host.");
        }

        _logger.LogInformation(
            "Agent 365 export enabled for agent {AgentAppId} in tenant {TenantId} "
            + "(endpoint={Endpoint}, offlineStorage={OfflineStorage}, perAgentOverrides={Overrides}). "
            + "Telemetry is discarded by the service unless a Microsoft 365 E7, Test - Microsoft 365 E7, "
            + "or Microsoft Agent 365 Frontier licence is ASSIGNED to at least one user in the tenant "
            + "(the SKU being present is not sufficient) and a tenant administrator has consented to "
            + "Agent365.Observability.OtelWrite.",
            config.AgentAppId,
            config.TenantId,
            config.UseS2SEndpoint ? "service-to-service" : "delegated",
            config.EnableOfflineStorage ? config.OfflineStorageDirectory : "disabled",
            config.Agents.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
