namespace Domain.Common.Config.AI.BundleExecution;

/// <summary>
/// Whether a bundle's own manifest may declare a <c>stdio</c> (local-command) MCP server, and how the
/// harness runs one when it does. Bound from <c>AppConfig:AI:BundleExecution:StdioMcpServers</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default, and deliberately separate from <see cref="BundleExecutionConfig.AllowBundleDeclaredMcpServers"/>.</strong>
/// That flag governs a remote (http/sse) server the host connects to over the network; this one governs a
/// local command the host launches as a process — a materially larger risk surface, run only inside the
/// Docker-tier sandbox (see <c>McpConnectionManager.StartSandboxedStdioSessionAsync</c>), never on the
/// host directly. An operator who has opted into remote bundle MCP servers has not thereby opted into
/// sandboxed local ones; each capability needs its own deliberate opt-in.
/// </para>
/// <para>
/// A bundle's stdio server is never given network access (see <c>ToolPermissionProfileResolver</c>'s
/// <c>ToolCapability.None</c> base grant for a bundle-owned name) — it must be fully self-contained
/// within the bundle's own staged files, which are copied into the sandbox workspace at session start.
/// </para>
/// </remarks>
public sealed class BundleStdioMcpServersConfig
{
    /// <summary>
    /// Master toggle for this capability. When disabled (the default), a bundle-declared <c>stdio</c>
    /// server is rejected at staging exactly as it was before this capability existed — the manifest is
    /// still parsed (to distinguish an explicit <c>stdio</c> declaration from a defaulted one for logging),
    /// but nothing is registered and no container is ever created.
    /// </summary>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// The container image a bundle-owned stdio server's sandbox session runs in. There is no per-bundle
    /// image override — a bundle's registered name contains a fresh GUID per staging, so the sandbox's
    /// existing per-tool <c>SandboxExecutionOptions.ToolOverrides</c> image lookup can never match one —
    /// so every bundle stdio server on a host shares this single operator-chosen runtime image. Still
    /// validated against <c>ContainerSandboxOptions.AllowedImagePrefixes</c> like any other image. Left
    /// empty, staging refuses to register a stdio server even when <see cref="Enabled"/> is true, since an
    /// empty image resolves to the harness's own default (.NET runtime) image, which cannot run most MCP
    /// servers.
    /// </summary>
    /// <value>Default: "" (no image configured — the capability stays inert until an operator sets one)</value>
    public string ContainerImage { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of distinct stdio MCP servers a single bundle may declare. Each is a separate
    /// container image/command a bundle registers — not a count of live sessions: since each concurrent
    /// <em>run</em> against the bundle's staged handle gets its own sandbox container per declared
    /// server (a stdio session cannot safely be shared across callers — see
    /// <c>Application.AI.Common.Services.Bundles.BundleRunIdAccessor</c>), the number of containers one
    /// bundle can pin at once is this value times however many of its runs are concurrent, further
    /// bounded by <see cref="MaxConcurrentSessions"/> host-wide. Must be positive.
    /// </summary>
    /// <value>Default: 2</value>
    public int MaxServersPerBundle { get; set; } = 2;

    /// <summary>
    /// Maximum number of bundle-owned stdio sandbox sessions live across the WHOLE host at once —
    /// distinct from <see cref="MaxServersPerBundle"/>, which bounds one bundle's distinct server
    /// declarations, not concurrently-live containers. Since each concurrent run of a staged bundle now
    /// gets its own session per declared server, this is the cap that actually bounds host-wide
    /// container count: it is what a caller admitted with upload + run permission is refused against
    /// once exceeded, regardless of how many bundles or runs are in play. Enforced in
    /// <c>McpConnectionManager.StartSandboxedStdioSessionAsync</c>. Must be positive.
    /// </summary>
    /// <value>Default: 8</value>
    public int MaxConcurrentSessions { get; set; } = 8;
}
