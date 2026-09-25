using Domain.Common.Config.Azure;

namespace Domain.Common.Config.Observability;

/// <summary>
/// Configuration for the Microsoft Agent 365 trace exporter, which publishes agent runs, tool
/// calls and inference calls into the tenant's Agent 365 control plane so the agent is visible
/// to Microsoft Defender, Microsoft Purview and the Microsoft 365 admin center rather than
/// running as an ungoverned "shadow agent".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in.</strong> <see cref="Enabled"/> defaults to <see langword="false"/>; a host that
/// omits this section is completely unaffected. Enabling it requires tenant-side provisioning that
/// the harness deliberately does not perform (see the operator prerequisites below).
/// </para>
/// <para>
/// <strong>Operator prerequisites — none of these are things the runtime can do for itself.</strong>
/// An Entra agent identity must already exist (created from an agent identity blueprint); the
/// blueprint must declare <c>Agent365.Observability.OtelWrite</c> on audience
/// <c>9b975845-388f-4429-889e-eab1ef63949c</c>; a tenant administrator must consent to it once; and
/// at least one user in the tenant must have a <em>Microsoft 365 E7</em>, <em>Test - Microsoft 365
/// E7</em> or <em>Microsoft Agent 365 Frontier</em> licence <strong>assigned</strong>. The SKU
/// merely existing in the tenant is not sufficient, and without an assigned licence the service
/// accepts requests and then discards the telemetry.
/// </para>
/// <para>
/// <strong>Independent of <c>AppConfig.AI.Identity</c>.</strong> The identity reported to Agent 365
/// comes from this section alone — <see cref="AgentAppId"/> and <see cref="Agents"/> — and not from
/// the harness's own agent-identity resolution. The two answer different questions: this one is the
/// agent's registered identity in the tenant directory, that one is which credential the agent
/// authenticates outbound calls with. Enabling either without the other is a valid configuration.
/// </para>
/// </remarks>
public class Agent365ExporterConfig
{
    /// <summary>
    /// Gets or sets whether the Agent 365 exporter is enabled.
    /// </summary>
    /// <value>Default: false. Requires tenant-side provisioning to be useful.</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the <c>appId</c> of the Entra <em>agent identity</em> this host runs as — not
    /// the blueprint's <c>appId</c>, and not the identity's object id.
    /// </summary>
    /// <remarks>
    /// Must be a GUID. The service validates this against the authenticated caller and rejects
    /// mismatches, and a non-GUID value makes the agent show as unidentified in the Agent 365
    /// dashboards, so <c>Agent365ExporterConfigValidator</c> rejects a non-GUID at startup.
    /// </remarks>
    public string? AgentAppId { get; set; }

    /// <summary>
    /// Gets or sets the Entra tenant id the agent identity belongs to. Must be a GUID.
    /// </summary>
    /// <remarks>
    /// This is the <em>agent's own</em> tenant, which is not necessarily the tenant of the human
    /// who initiated the work. Agent identities are single-tenant and can only be issued tokens in
    /// the tenant where they were created.
    /// </remarks>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the <c>appId</c> of the agent identity blueprint this agent was created from.
    /// Optional; supplied only so Agent 365 can group every agent of the same kind. Must be a GUID
    /// when set.
    /// </summary>
    public string? BlueprintId { get; set; }

    /// <summary>
    /// Gets or sets the display name reported for this agent. Optional; falls back to the agent's
    /// own name from the execution scope when unset.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Gets or sets per-agent identity overrides, keyed by the harness agent's name, for hosts that
    /// run more than one distinct agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An agent whose name appears here reports as its own Entra agent identity; every other agent in
    /// the process falls back to <see cref="AgentAppId"/> and <see cref="BlueprintId"/>. Leave this
    /// empty for a single-agent host.
    /// </para>
    /// <para>
    /// Keys are matched case-insensitively against the running agent's name. An unmatched agent is
    /// <em>not</em> an error — it uses the host default — so a typo here degrades silently to the
    /// default identity rather than failing. That is the reason the name is echoed in the
    /// startup log: it is the only place a mistyped key is visible.
    /// </para>
    /// </remarks>
    public Dictionary<string, Agent365AgentIdentityConfig> Agents { get; set; } = [];

    /// <summary>
    /// Gets or sets the Entra credentials the exporter authenticates with when acquiring its
    /// <c>Agent365.Observability.OtelWrite</c> token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Follows the same shape as every other Entra-authenticating integration in the harness
    /// (<c>AIFoundryConfig.Entra</c>, <c>GraphApiConfig.Entra</c>, the Purview provider sections) and
    /// is resolved through <c>AzureCredentialFactory.CreateTokenCredential</c>, so it inherits the
    /// established credential hierarchy — explicit client secret, then client certificate, then
    /// <c>DefaultAzureCredential</c> — rather than introducing a second one.
    /// </para>
    /// <para>
    /// Leaving this empty is the recommended production shape: an agent hosted on Azure whose
    /// managed identity is federated to the agent identity blueprint needs no configured secret, and
    /// <c>DefaultAzureCredential</c> picks the managed identity up automatically. Credentials belong
    /// to the <em>blueprint</em>, not the agent identity, so every agent minted from a blueprint
    /// shares them — which is why Microsoft treats a blueprint as a credential boundary.
    /// </para>
    /// </remarks>
    public EntraCredentialConfig Auth { get; set; } = new();

    /// <summary>
    /// Gets or sets whether to use the service-to-service endpoint rather than the delegated one.
    /// </summary>
    /// <value>
    /// Default: true. The harness runs autonomous, unattended work, which is the service-to-service
    /// case. The SDK's own default is <see langword="false"/> (the delegated path), so this is an
    /// intentional divergence — leaving it false sends S2S traffic to the delegated route.
    /// </value>
    public bool UseS2SEndpoint { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the exporter may persist undeliverable telemetry to local disk and
    /// replay it on a background loop.
    /// </summary>
    /// <value>
    /// Default: false — deliberately the opposite of the SDK's default.
    /// </value>
    /// <remarks>
    /// <para>
    /// The SDK enables on-disk store-and-forward by default and, with no
    /// <see cref="OfflineStorageDirectory"/> set, resolves a location under <c>LOCALAPPDATA</c> or
    /// <c>TEMP</c> on Windows and <c>TMPDIR</c>, <c>/var/tmp</c> or <c>/tmp</c> elsewhere.
    /// </para>
    /// <para>
    /// The harness inverts that default on purpose. Agent spans can carry prompts, tool arguments
    /// and model output (governed by <c>AppConfig.AI.Telemetry.ContentCapture</c>), so inheriting
    /// the SDK default would quietly write conversation content into a per-user temp directory. The
    /// failure mode compounds: while a tenant is unlicensed or consent has not been granted,
    /// <em>every</em> export fails, so <em>everything</em> is persisted rather than an occasional
    /// retry batch.
    /// </para>
    /// <para>
    /// The trade-off is accepted knowingly: with this off, a transient network failure loses that
    /// telemetry instead of retrying it. Losing spans is preferable to writing conversation content
    /// somewhere the deployment did not choose. A consumer that wants replay should enable this
    /// <em>and</em> set <see cref="OfflineStorageDirectory"/> to a location it controls.
    /// </para>
    /// </remarks>
    public bool EnableOfflineStorage { get; set; }

    /// <summary>
    /// Gets or sets the directory used for offline store-and-forward when
    /// <see cref="EnableOfflineStorage"/> is set. Ignored otherwise.
    /// </summary>
    /// <remarks>
    /// Required when <see cref="EnableOfflineStorage"/> is set: the harness will not fall back to
    /// the SDK's temp-directory default, because that choice belongs to the deployment rather than
    /// to a library. Point this at a path with owner-only permissions — it may contain conversation
    /// content.
    /// </remarks>
    public string? OfflineStorageDirectory { get; set; }
}
