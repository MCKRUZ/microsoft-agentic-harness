using Domain.Common.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using OpenTelemetry.Context.Propagation;

namespace Infrastructure.Observability;

/// <summary>
/// Startup validator for the host-wide baggage-egress policy (<see cref="Domain.Common.Config.Observability.ObservabilityConfig.PropagateBaggage"/>,
/// #738). Refuses to boot a host that expects baggage suppressed but finds it isn't.
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
/// </remarks>
public sealed class BaggageEgressStartupValidator : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initializes a new instance of the <see cref="BaggageEgressStartupValidator"/> class.</summary>
    public BaggageEgressStartupValidator(IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var observability = _config.CurrentValue.Observability;

        // Baggage present in either propagator is exactly correct, not a regression, when the
        // operator explicitly opted into cross-process baggage — this validator has nothing to
        // assert in that case.
        if (observability.PropagateBaggage)
        {
            return Task.CompletedTask;
        }

        var otelPropagatedFields = Propagators.DefaultTextMapPropagator.Fields;
        if (otelPropagatedFields?.Contains("baggage") == true)
        {
            throw new InvalidOperationException(
                "This host's baggage-egress policy expects baggage suppressed "
                + "(Observability:PropagateBaggage is false), but the process's default OpenTelemetry "
                + "text-map propagator carries baggage — meaning any ambient OpenTelemetry baggage "
                + "would cross this host's process boundary on outbound HTTP calls, and a "
                + "caller-supplied baggage header would be accepted as attacker-chosen attribution on "
                + "inbound ones. AddOpenTelemetry sets a trace-context-only propagator when this flag "
                + "is false; something registered afterwards changed it. Remove whatever re-registers "
                + "the propagator, or set Observability:PropagateBaggage to true if this host has a "
                + "deliberate, reviewed reason to propagate baggage.");
        }

        var activityPropagatedFields = DistributedContextPropagator.Current.Fields;
        if (activityPropagatedFields?.Contains("baggage") == true)
        {
            throw new InvalidOperationException(
                "This host's baggage-egress policy expects baggage suppressed "
                + "(Observability:PropagateBaggage is false), but the process's default "
                + "System.Diagnostics.Activity propagator carries baggage — meaning any ambient "
                + "identity attribution (tenant, agent, blueprint and conversation ids, if this host "
                + "runs Agent 365) would cross this host's process boundary on outbound HTTP calls, "
                + "and a caller-supplied baggage header would be accepted as attacker-chosen "
                + "attribution on inbound ones. AddOpenTelemetry sets DistributedContextPropagator.Current "
                + "to a trace-context-only propagator when this flag is false; something registered "
                + "afterwards changed it. Remove whatever re-registers the propagator, or set "
                + "Observability:PropagateBaggage to true if this host has a deliberate, reviewed "
                + "reason to propagate baggage.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
