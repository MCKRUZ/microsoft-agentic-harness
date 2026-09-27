using Application.Common.Factories;
using Azure.Core;
using Domain.Common.Config;
using Microsoft.OpenTelemetry;
using OpenTelemetry;

namespace Presentation.Common.Extensions;

/// <summary>
/// Wires the Microsoft Agent 365 trace exporter onto the OpenTelemetry pipeline, so this host's
/// agent runs, tool calls and inference calls appear in the tenant's agent control plane
/// (Microsoft Defender, Microsoft Purview, the Microsoft 365 admin center) instead of the agent
/// running as an ungoverned "shadow agent".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the whole-distro entry point, narrowed.</strong> The SDK has a targeted
/// <c>UseAgent365</c> internally, but it is not publicly accessible, so
/// <c>UseMicrosoftOpenTelemetry</c> is the only supported way in. That call would otherwise
/// configure Azure Monitor, OTLP, Console <em>and</em> infrastructure instrumentation alongside
/// Agent 365 — all of which this harness already composes itself through an ordered
/// <c>ITelemetryConfigurator</c> chain with its own processors. It is therefore narrowed here to
/// exactly one export target and every bundled instrumentation explicitly switched off, so it
/// contributes the Agent 365 pipeline and nothing that would duplicate what the harness registers.
/// </para>
/// <para>
/// The explicit <see langword="false"/> values matter rather than being belt-and-braces: the SDK
/// only forces <em>unset</em> instrumentation options off in Agent-365-only mode, and preserves
/// anything the caller set deliberately — so being explicit is also what keeps the behaviour stable
/// if this host ever combines Agent 365 with another target.
/// </para>
/// <para>
/// <strong>The baggage-to-tags processor is the load-bearing part.</strong> The exporter identifies
/// every span by agent id and tenant id, and refuses (silently) to export a span that carries
/// neither. Those values are published as OpenTelemetry baggage at the start of an agent turn; the
/// processor the vendor registers here copies them onto each span as it starts, on the turn's own
/// thread. That ordering matters: an exporter runs on a background batching thread where ambient
/// context is empty, so reading baggage at export time would find nothing.
/// </para>
/// </remarks>
public static class Agent365TelemetryExtensions
{
    /// <summary>
    /// The OAuth scope the exporter's access token must carry. Agent 365 rejects a token without
    /// the matching app role (service-to-service) or delegated scope.
    /// </summary>
    /// <remarks>
    /// The audience GUID is Agent 365 Observability's own resource id and is a fixed, Microsoft-owned
    /// value rather than anything tenant-specific — it is the same for every customer, which is why
    /// it is a constant here instead of configuration.
    /// </remarks>
    internal const string ObservabilityScope =
        "api://9b975845-388f-4429-889e-eab1ef63949c/Agent365.Observability.OtelWrite";

    /// <summary>
    /// Adds the Agent 365 exporter to <paramref name="builder"/> when
    /// <c>AppConfig.Observability.Exporters.Agent365.Enabled</c> is set. No-op when it is not, so a
    /// host that has not opted in registers nothing at all.
    /// </summary>
    /// <param name="builder">The OpenTelemetry builder returned by <c>AddOpenTelemetry()</c>.</param>
    /// <param name="appConfig">Application configuration carrying the exporter section.</param>
    /// <returns>The supplied builder, for chaining.</returns>
    public static IOpenTelemetryBuilder AddAgent365Exporter(
        this IOpenTelemetryBuilder builder,
        AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(appConfig);

        var config = appConfig.Observability.Exporters.Agent365;
        if (!config.Enabled)
        {
            return builder;
        }

        // This method DEPENDS ON the host's baggage-egress policy rather than owning it (#738).
        // Agent 365 attribution rides baggage — originally believed to be OpenTelemetry's own Baggage
        // API only, until a security review found the harness's own identity attribution (tenant id,
        // agent app id, blueprint id, conversation id) actually rides System.Diagnostics.Activity's
        // separate baggage store, which no OpenTelemetry component touches. A propagator that carries
        // either would serialise that onto every outbound HTTP call and accept a caller-supplied
        // version on every inbound one. That protection used to live here, as an unnamed side effect
        // gated on this exporter's own Enabled flag — which meant a host running some other integration
        // that also touches baggage got no protection at all, and an operator had no flag to check. It
        // is now ObservabilityConfig.PropagateBaggage, applied once in AddOpenTelemetry for every host
        // shape and BOTH baggage stores, default false, so the same protection covers any future
        // baggage use, not just this one. BaggageEgressStartupValidator — a standalone validator,
        // registered unconditionally and gated only on PropagateBaggage itself, not on this exporter's
        // Enabled flag — re-asserts both propagators at boot in case something else in the composition
        // re-registers baggage-carrying propagation afterwards.

        // Built once here rather than per export. Azure.Identity credentials cache the token
        // in-process and refresh shortly before expiry, so the per-batch GetTokenAsync below is
        // near-instant — which is what the exporter's contract requires of a token resolver ("must
        // be fast and non-blocking"). This is the same reasoning EntraTokenAuthHandler relies on for
        // outbound MCP calls, and it goes through the shared AzureCredentialFactory so the exporter
        // inherits the harness's existing credential hierarchy (explicit secret, then certificate,
        // then DefaultAzureCredential/managed identity) rather than introducing a second one.
        // The tenant the token is acquired in must be the tenant reported in the telemetry. These are
        // two separate settings — the credential section has its own TenantId — and nothing otherwise
        // ties them together. Left blank (the documented recommended shape, where a managed identity
        // federated to the blueprint needs no explicit credentials) DefaultAzureCredential acquires a
        // token in whatever tenant the ambient credential defaults to: a developer's az-login tenant,
        // or a managed identity in a different directory. The payload's tenant then disagrees with the
        // token's, and Agent 365 drops those spans without reporting anything. Defaulting it here means
        // the operator sets the tenant once and the two cannot silently diverge.
        var auth = config.Auth;
        if (string.IsNullOrWhiteSpace(auth.TenantId) && !string.IsNullOrWhiteSpace(config.TenantId))
        {
            auth = new Domain.Common.Config.Azure.EntraCredentialConfig
            {
                TenantId = config.TenantId,
                ClientId = auth.ClientId,
                ClientSecret = auth.ClientSecret,
                CertificatePath = auth.CertificatePath,
                ExcludeManagedIdentityCredential = auth.ExcludeManagedIdentityCredential,
            };
        }

        var credential = AzureCredentialFactory.CreateTokenCredential(auth);

        return builder.UseMicrosoftOpenTelemetry(distro =>
        {
            // Exactly one target. Azure Monitor, OTLP, Prometheus and Console are all wired by the
            // harness's own ITelemetryConfigurator chain; naming them here would register them twice.
            distro.Exporters = ExportTarget.Agent365;

            // Agent Framework's own spans are already collected — AiTelemetryConfigurator subscribes
            // to its activity source — so the distro must not add a second subscription. Everything
            // else here is infrastructure instrumentation this harness registers itself.
            distro.Instrumentation.EnableAspNetCoreInstrumentation = false;
            distro.Instrumentation.EnableHttpClientInstrumentation = false;
            distro.Instrumentation.EnableSqlClientInstrumentation = false;
            distro.Instrumentation.EnableAzureSdkInstrumentation = false;
            distro.Instrumentation.EnableAgentFrameworkInstrumentation = false;
            distro.Instrumentation.EnableSemanticKernelInstrumentation = false;
            distro.Instrumentation.EnableOpenAIInstrumentation = false;

            var options = distro.Agent365;

            options.TokenResolver = async (_, _) =>
            {
                // Returning null omits the Authorization header, which the exporter treats as "skip this
                // batch" and logs. Swallowing here rather than propagating is deliberate: this runs on
                // the exporter's own background batch thread, where an escaping exception is an
                // unobserved failure inside a third-party pipeline rather than something a caller can
                // handle. Telemetry must never be able to destabilise the process that produces it.
                //
                // Worth knowing: the FIRST call is not necessarily fast. With no explicit credentials
                // configured this resolves DefaultAzureCredential, whose first acquisition walks the
                // credential chain — including managed-identity probe timeouts. Subsequent calls are
                // served from Azure.Identity's in-process cache, which is what makes the per-batch call
                // cheap thereafter.
                try
                {
                    var token = await credential
                        .GetTokenAsync(new TokenRequestContext([ObservabilityScope]), CancellationToken.None)
                        .ConfigureAwait(false);

                    return token.Token;
                }
                catch (Exception)
                {
                    return null;
                }
            };

            // The SDK defaults this to false, which routes to the delegated endpoint. The harness
            // runs unattended work authenticating as the agent identity itself, which is the
            // service-to-service case, and the two endpoints are different URL paths — so leaving
            // the default would send S2S traffic somewhere that will not accept it.
            options.UseS2SEndpoint = config.UseS2SEndpoint;

            // Inverted relative to the SDK, which persists undeliverable spans by default beneath
            // LOCALAPPDATA/TEMP. Harness spans can carry prompts, tool arguments and model output, so
            // that location is the deployment's choice to make, not a library's. Validation
            // guarantees a directory is named whenever this is enabled.
            options.DisableOfflineStorage = !config.EnableOfflineStorage;
            if (config.EnableOfflineStorage)
            {
                options.StorageDirectory = config.OfflineStorageDirectory;
            }
        });
    }
}
