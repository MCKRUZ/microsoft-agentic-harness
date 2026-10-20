namespace Tests.Common;

/// <summary>
/// The cross-assembly references every presentation host is known to ship unresolved, each with the reason
/// it is accepted. Hosts that share the libraries share the entries; a host with an extra one adds it.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is an accepted risk, not a clean bill of health. <see cref="PackageSkewGuard"/> fails when an
/// entry stops matching, so fixing the underlying skew forces the entry out. "No call site" claims below
/// come from a text search of the harness source, which cannot see a call made through a package, so they
/// are stated as that and not as a guarantee. Follow-ups are carried by the maintainer, not filed as issues,
/// so none carries a number except where one exists.
/// </para>
/// <para>
/// The Anthropic.SDK and Azure.AI.AgentServer.Core entries are real package skew, found by
/// the guard, that the hand-written surface tests did not reach. They name the one member that is broken,
/// not its type, so a second, different break on the same type still fails. The entries for packages that
/// are not shipped are prefixes, bound to <see cref="MemberReferenceResolver.Kind.MissingAssembly"/>.
/// </para>
/// </remarks>
public static class KnownHostSkews
{
    /// <summary>The entries every host shares.</summary>
    public static readonly IReadOnlyList<KnownSkew> Shared =
    [
        new(
            "Anthropic.SDK",
            "Microsoft.Extensions.AI.HostedMcpServerTool::get_AuthorizationToken",
            "Skew: Anthropic.SDK 5.10.0 was built against a Microsoft.Extensions.AI that exposed "
            + "HostedMcpServerTool.AuthorizationToken, which the loaded version no longer has. It surfaces only "
            + "when an Anthropic-routed agent is handed a hosted MCP tool; a text search finds no harness code "
            + "that constructs one. Carried follow-up: move Anthropic.SDK to a build that matches."),
        new(
            "HealthChecks.UI",
            "k8s.",
            "Kubernetes service discovery. KubernetesClient is pinned to a patched 19.x for an advisory "
            + "(Presentation.Common.csproj) and HealthChecks.UI was built against 15.x. Used only when "
            + "Kubernetes discovery is configured; a text search finds no such configuration in the harness."),
        new(
            "HealthChecks.UI",
            "System.Net.Http.BasicAuthenticationHeaderValue::",
            "HealthChecks.UI's optional OIDC/basic-auth client needs the IdentityModel package, which no "
            + "host ships. Reached only when the UI is configured with that authentication; a text search "
            + "finds none. Accepted only while the assembly is absent: if IdentityModel ships, a missing "
            + "member in it is a real failure.",
            MemberReferenceResolver.Kind.MissingAssembly),
        new(
            "Microsoft.OpenTelemetry",
            "Microsoft.Agents.Builder.",
            "Microsoft.OpenTelemetry's Agent 365 hosting integration compiles against Microsoft.Agents.Builder, "
            + "which is optional and not shipped. The harness uses the attribution API (baggage), not that "
            + "hosting path. Accepted only while the assembly is absent; becomes a real failure if the hosting "
            + "integration is adopted.",
            MemberReferenceResolver.Kind.MissingAssembly),
        new(
            "Microsoft.OpenTelemetry",
            "Microsoft.Agents.Core.",
            "As above, for Microsoft.Agents.Core. Deliberately not a prefix over Microsoft.Agents.*: the hosts "
            + "do ship Microsoft.Agents.AI.*, and a broken call into those must not be excused by this entry.",
            MemberReferenceResolver.Kind.MissingAssembly),
    ];

    /// <summary>
    /// The entries for the audit archiver host (#416). It references no presentation library, so it ships neither
    /// HealthChecks.UI nor Microsoft.OpenTelemetry, and those entries would match nothing and fail as stale.
    /// </summary>
    public static readonly IReadOnlyList<KnownSkew> AuditArchiver =
        [.. Shared.Where(s => s.Assembly is not ("HealthChecks.UI" or "Microsoft.OpenTelemetry"))];

    /// <summary>The entries for the Foundry hosting host: the shared ones plus its own.</summary>
    public static readonly IReadOnlyList<KnownSkew> FoundryHost =
    [
        .. Shared,
        new(
            "Azure.AI.AgentServer.Core",
            "Microsoft.OpenTelemetry.Agent365Options::get_Exporter",
            "Skew: Azure.AI.AgentServer.Core was built against a Microsoft.OpenTelemetry whose "
            + "Agent365Options.Exporter has since changed shape. Reached only when the AgentServer's Agent 365 "
            + "export is switched on. Carried follow-up: align the two packages before enabling it."),
    ];
}
