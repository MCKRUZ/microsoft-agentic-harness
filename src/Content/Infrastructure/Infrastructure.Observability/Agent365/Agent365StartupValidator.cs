using System.Reflection;
using Application.Common.Interfaces.Common;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
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
    private readonly IOwnerOnlyDirectoryCreator _directoryCreator;

    /// <summary>Initializes a new instance of the <see cref="Agent365StartupValidator"/> class.</summary>
    public Agent365StartupValidator(
        IOptionsMonitor<AppConfig> config,
        ILogger<Agent365StartupValidator> logger,
        IOwnerOnlyDirectoryCreator directoryCreator)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(directoryCreator);
        _config = config;
        _logger = logger;
        _directoryCreator = directoryCreator;
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

        ValidateWebTelemetryHost(observability);
        WarnIfBaggageEgressReopened(observability, config);
        EnforceOfflineStorageDirectory(config);
        LogEnabled(config);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Refuses to boot a host that has enabled Agent 365 export but is not listed as a web-telemetry
    /// project, since the exporter attaches to a pipeline only that host shape builds.
    /// </summary>
    /// <remarks>
    /// Reads the shared rule on <see cref="ObservabilityConfig"/>, not a second copy of it. This
    /// validator's whole premise is that its answer equals the one the telemetry composition takes;
    /// two independently maintained expressions would make that a coincidence, and a drifted copy
    /// would restore the silent failure this exists to prevent.
    /// </remarks>
    private static void ValidateWebTelemetryHost(ObservabilityConfig observability)
    {
        var entryAssembly = Assembly.GetEntryAssembly()?.GetName().Name ?? "UnknownService";

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
    }

    /// <summary>
    /// Warns — does not refuse to boot — when this host has deliberately opted into both Agent 365
    /// export and cross-process baggage propagation together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The baggage-egress re-assertion that used to live here moved to its own
    /// <c>BaggageEgressStartupValidator</c> (#738's altitude pass). That policy is host-wide, not an
    /// Agent-365 concern — nesting the check behind this validator's Agent-365-specific early return
    /// meant a host with Agent 365 disabled got none of the protection its own doc comment promises.
    /// See that validator's remarks for the reasoning; it is registered unconditionally, gated only on
    /// the policy flag itself.
    /// </para>
    /// <para>
    /// The one combination that policy's re-assertion cannot see: a host that DELIBERATELY set
    /// <c>PropagateBaggage</c> to <c>true</c> (so <c>BaggageEgressStartupValidator</c> has nothing to
    /// assert) while also running Agent 365 — which silently re-opens the exact egress this feature's
    /// baggage suppression used to make structurally impossible. Not a boot refusal (the operator did
    /// ask for baggage propagation), but silent is the wrong default for a combination this much worse
    /// than either setting alone — a security review of #738 flagged the silence itself.
    /// </para>
    /// </remarks>
    private void WarnIfBaggageEgressReopened(ObservabilityConfig observability, Agent365ExporterConfig config)
    {
        if (!observability.PropagateBaggage)
        {
            return;
        }

        _logger.LogWarning(
            "Agent 365 export and Observability:PropagateBaggage are BOTH enabled. Agent 365 "
            + "attribution (tenant {TenantId}, agent {AgentAppId}, blueprint and conversation ids) "
            + "rides baggage, so it will now be serialised onto every outbound HTTP call this host "
            + "makes — LLM providers, third-party MCP servers, web-fetch targets. Set "
            + "PropagateBaggage to false unless this egress is a reviewed, intended choice.",
            config.TenantId,
            config.AgentAppId);
    }

    /// <summary>
    /// Forces owner-only permissions on the offline-storage directory (#738) rather than merely
    /// document the requirement.
    /// </summary>
    /// <remarks>
    /// The vendor SDK creates this directory itself and chooses its own mode; our own code never
    /// touched it before this. Failure refuses boot rather than silently proceeding with a directory
    /// this process cannot confirm is owner-only, because the content spilled there can include
    /// prompts and tool arguments — a consumer who set <c>EnableOfflineStorage</c> to <c>true</c> asked
    /// for it to exist, and existing it insecurely is worse than not booting. A no-op on Windows, left
    /// to its inherited ACL — see <see cref="IOwnerOnlyDirectoryCreator"/>.
    /// </remarks>
    private void EnforceOfflineStorageDirectory(Agent365ExporterConfig config)
    {
        if (!config.EnableOfflineStorage)
        {
            return;
        }

        // Agent365ExporterConfigValidator enforces OfflineStorageDirectory as non-empty when
        // EnableOfflineStorage is true — but this repo has a documented, repeated failure mode of a
        // config validator silently going unbound (CLAUDE.md's "shipping a control that nothing
        // invokes" entry lists six prior instances). If that ever happens here, this check turns an
        // opaque path-parsing exception into the same clear boot refusal every other Agent 365
        // misconfiguration produces, rather than depending on a second validator actually having run.
        if (string.IsNullOrWhiteSpace(config.OfflineStorageDirectory))
        {
            throw new InvalidOperationException(
                "Agent 365 offline storage is enabled "
                + "(Observability:Exporters:Agent365:EnableOfflineStorage = true) but "
                + "Observability:Exporters:Agent365:OfflineStorageDirectory is blank. Set it to a "
                + "directory this process can secure as owner-only, or disable offline storage.");
        }

        try
        {
            _directoryCreator.Create(config.OfflineStorageDirectory, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Agent 365 offline storage is enabled "
                + $"(Observability:Exporters:Agent365:OfflineStorageDirectory = "
                + $"'{config.OfflineStorageDirectory}') but the directory could not be created or "
                + "confirmed as owner-only. This directory can hold prompts and tool arguments spilled "
                + "from failed exports, so the harness refuses to proceed with a directory it cannot "
                + "secure. See the inner exception for the specific path and cause.",
                ex);
        }
    }

    private void LogEnabled(Agent365ExporterConfig config)
        => _logger.LogInformation(
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
}
