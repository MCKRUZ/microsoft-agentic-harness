using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using OpenTelemetry.Context.Propagation;

namespace Infrastructure.Observability;

/// <summary>
/// Startup validator for the host-wide baggage-egress policy (<see cref="ObservabilityConfig.PropagateBaggage"/>,
/// #738). Refuses to boot a host that expects baggage suppressed but finds it isn't, and warns when an
/// operator has deliberately opted into egress.
/// </summary>
/// <remarks>
/// <para>
/// This check used to live inside <c>Agent365.Agent365StartupValidator</c>, nested behind that
/// validator's own early return when Agent 365 is disabled. That was the wrong altitude: the policy's
/// own doc comment states it is "a host-wide egress policy, not an Agent 365 concern... so a consumer
/// who adds their own baggage usage still gets this protection" — but a host with Agent 365 disabled
/// got none of that protection, because the only code that ever re-checked the propagator was gated on
/// an unrelated flag. Found by the altitude pass of #738's own review. Registered unconditionally and
/// gated only on <c>PropagateBaggage</c> itself, so every host this policy is meant to protect actually
/// gets checked, not only the ones that happen to also enable Agent 365.
/// </para>
/// <para>
/// <c>AddOpenTelemetry</c> already installs the correct propagator for whichever value of
/// <c>PropagateBaggage</c> is configured. This validator exists for the case that matters after
/// composition: something running later — a consumer's own <c>Program.cs</c>, a library that calls
/// <c>Sdk.SetDefaultTextMapPropagator</c> with a composite propagator of its own — silently
/// re-enables baggage egress. Nothing downstream would notice, because a working propagator that
/// happens to leak more is not an error to anything not specifically checking. This is that check,
/// run once, at the last point before the host starts serving traffic.
/// </para>
/// <para>
/// There are TWO independent baggage stores, each with its own propagator, and this validator checks
/// both: OpenTelemetry's own <c>Baggage</c> API (governed by
/// <see cref="Propagators.DefaultTextMapPropagator"/>) and <see cref="Activity.Baggage"/> (governed by
/// <see cref="DistributedContextPropagator.Current"/>) — the one this harness's own identity
/// attribution (user id, conversation id) actually rides via <c>Activity.AddBaggage</c>. A security
/// review of #738 found the original implementation checked only the first, leaving the store this
/// harness actually uses completely unguarded. Detection is a field-name match against each
/// propagator's own known baggage-carrying field names, so a differently-named baggage-carrying
/// propagator a consumer might register (a Jaeger-style <c>uberctx-*</c> propagator, for example)
/// would not be caught by either check.
/// </para>
/// <para>
/// The opted-in ("PropagateBaggage: true") case is not a boot refusal — the operator asked for this —
/// but it is not silent either. This host's own identity attribution (conversation and user ids, via
/// <c>Activity.AddBaggage</c> call sites that have no Agent 365 dependency at all) now egresses on
/// every outbound call regardless of whether Agent 365 is involved, so the warning fires here,
/// unconditionally on the flag alone, with Agent 365's tenant/agent/blueprint ids named as an additive
/// detail only when that exporter also happens to be enabled. An earlier cut of this warning lived
/// inside <c>Agent365StartupValidator</c>, gated on that exporter's own <c>Enabled</c> flag — the exact
/// same wrong-altitude mistake this file's own OTel/Activity split was created to fix, just one layer
/// further down; found by a second altitude pass on the same diff.
/// </para>
/// </remarks>
public sealed class BaggageEgressStartupValidator : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<BaggageEgressStartupValidator> _logger;

    /// <summary>Initializes a new instance of the <see cref="BaggageEgressStartupValidator"/> class.</summary>
    public BaggageEgressStartupValidator(
        IOptionsMonitor<AppConfig> config,
        ILogger<BaggageEgressStartupValidator> logger)
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

        // Baggage present in either propagator is exactly correct, not a regression, when the
        // operator explicitly opted into cross-process baggage — this validator has nothing to
        // refuse in that case, only something to name.
        if (observability.PropagateBaggage)
        {
            WarnBaggagePropagationEnabled(observability);
            return Task.CompletedTask;
        }

        AssertPropagatorDoesNotCarryBaggage(
            "OpenTelemetry",
            Propagators.DefaultTextMapPropagator.Fields,
            "any ambient OpenTelemetry baggage",
            "AddOpenTelemetry sets a trace-context-only propagator");

        AssertPropagatorDoesNotCarryBaggage(
            "System.Diagnostics.Activity",
            DistributedContextPropagator.Current.Fields,
            "any ambient identity attribution (tenant, agent, blueprint and conversation ids, if this "
            + "host runs Agent 365)",
            "AddOpenTelemetry sets DistributedContextPropagator.Current to a trace-context-only propagator");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Throws naming <paramref name="propagatorLabel"/> when <paramref name="fields"/> — that
    /// propagator's own declared field names — includes "baggage" or its legacy "Correlation-Context"
    /// alias.
    /// </summary>
    /// <remarks>
    /// Both names are checked, matching <c>BaggageSuppressingDistributedContextPropagator</c>'s own
    /// <c>SuppressedFieldNames</c> — that type treats both as equally sensitive and suppresses both, so
    /// this re-assertion must recognise both too. A propagator whose <c>Fields</c> carried
    /// "Correlation-Context" without the literal string "baggage" would otherwise pass this check while
    /// still egressing identity via that legacy header. The pinned runtime's own W3C propagator always
    /// bundles both names together, so this gap does not fire against today's default — but the check
    /// should not depend on that coincidence.
    /// </remarks>
    private static void AssertPropagatorDoesNotCarryBaggage(
        string propagatorLabel,
        IEnumerable<string>? fields,
        string leakDetail,
        string setterDescription)
    {
        if (fields is null
            || (!fields.Contains("baggage") && !fields.Contains("Correlation-Context")))
        {
            return;
        }

        throw new InvalidOperationException(
            "This host's baggage-egress policy expects baggage suppressed "
            + $"(Observability:PropagateBaggage is false), but the process's default {propagatorLabel} "
            + $"propagator carries baggage — meaning {leakDetail} would cross this host's process "
            + "boundary on outbound HTTP calls, and a caller-supplied baggage header would be accepted "
            + $"as attacker-chosen attribution on inbound ones. {setterDescription} when this flag is "
            + "false; something registered afterwards changed it. Remove whatever re-registers the "
            + "propagator, or set Observability:PropagateBaggage to true if this host has a deliberate, "
            + "reviewed reason to propagate baggage.");
    }

    /// <summary>
    /// Warns that this host's identity attribution now egresses on every outbound call, naming Agent
    /// 365's own attribution as an additive detail when that exporter is also enabled.
    /// </summary>
    private void WarnBaggagePropagationEnabled(ObservabilityConfig observability)
    {
        var agent365 = observability.Exporters.Agent365;

        if (agent365.Enabled)
        {
            _logger.LogWarning(
                "Observability:PropagateBaggage is enabled, and Agent 365 export is ALSO enabled. Agent "
                + "365 attribution (tenant {TenantId}, agent {AgentAppId}, blueprint and conversation "
                + "ids) rides baggage, so it will now be serialised onto every outbound HTTP call this "
                + "host makes — LLM providers, third-party MCP servers, web-fetch targets — alongside "
                + "any other identity this process places into baggage. Set PropagateBaggage to false "
                + "unless this egress is a reviewed, intended choice.",
                agent365.TenantId,
                agent365.AgentAppId);
            return;
        }

        _logger.LogWarning(
            "Observability:PropagateBaggage is enabled. Any identity this process places into baggage "
            + "(conversation id, user id, or anything a consumer's own code adds via Activity.AddBaggage) "
            + "will now be serialised onto every outbound HTTP call this host makes — LLM providers, "
            + "third-party MCP servers, web-fetch targets — and a caller-supplied baggage header will be "
            + "accepted as attacker-chosen attribution on inbound ones. Set PropagateBaggage to false "
            + "unless this egress is a reviewed, intended choice.");
    }
}
