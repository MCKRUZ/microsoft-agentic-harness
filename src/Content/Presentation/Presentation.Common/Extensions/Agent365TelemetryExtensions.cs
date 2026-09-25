using Application.Common.Factories;
using Azure.Core;
using Domain.Common.Config;
using Microsoft.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

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

        // Stop OpenTelemetry baggage crossing the process boundary in either direction.
        //
        // OUTBOUND: the default propagator serialises baggage into a `baggage` HTTP header and the
        // HTTP-client instrumentation attaches it to every outbound call, so the tenant id, agent
        // app id, blueprint id and conversation id published for a turn would otherwise be sent to
        // LLM providers, third-party MCP servers and web-fetch targets. None of those is a
        // credential, but they identify the customer's tenant and its agents to parties that have no
        // business receiving them, and enabling agent governance must not be the thing that starts
        // leaking them.
        //
        // INBOUND: the same propagator extracts a caller-supplied `baggage` header into the ambient
        // context, where the vendor's baggage-to-tags processor would stamp caller-chosen agent and
        // tenant ids onto spans. The service validates the agent id against this host's token, so the
        // ceiling is junk entries in governance records rather than impersonation — but there is no
        // reason to accept it.
        //
        // Trace context (traceparent/tracestate) is unaffected, so distributed tracing across
        // services keeps working; only baggage stops being propagated. Scoped to hosts that enable
        // Agent 365 rather than applied globally, so a host that has not opted in keeps whatever
        // propagation behaviour it has today.
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

        // Built once here rather than per export. Azure.Identity credentials cache the token
        // in-process and refresh shortly before expiry, so the per-batch GetTokenAsync below is
        // near-instant — which is what the exporter's contract requires of a token resolver ("must
        // be fast and non-blocking"). This is the same reasoning EntraTokenAuthHandler relies on for
        // outbound MCP calls, and it goes through the shared AzureCredentialFactory so the exporter
        // inherits the harness's existing credential hierarchy (explicit secret, then certificate,
        // then DefaultAzureCredential/managed identity) rather than introducing a second one.
        var credential = AzureCredentialFactory.CreateTokenCredential(config.Auth);

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
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext([ObservabilityScope]), CancellationToken.None)
                    .ConfigureAwait(false);

                return token.Token;
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
