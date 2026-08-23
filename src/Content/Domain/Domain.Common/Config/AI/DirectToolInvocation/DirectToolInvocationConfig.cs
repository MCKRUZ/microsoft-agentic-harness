namespace Domain.Common.Config.AI.DirectToolInvocation;

/// <summary>
/// Root configuration for direct tool invocation — the host's ability to execute one of its own
/// registered tools synchronously on behalf of an HTTP caller. Bound from
/// <c>AppConfig:AI:DirectToolInvocation</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default, and unlike its neighbours it stays off in every shipped host.</strong>
/// <c>BundleExecution</c> and <c>WorkflowSubmission</c> both set <c>Enabled: true</c> in
/// <c>Presentation.ExecutionApi</c>, because serving those APIs is that host's purpose. This one does
/// not, and the difference is deliberate: those surfaces run an <em>agent</em> that may choose to use a
/// tool, whereas this one lets a caller name the tool and the operation directly. That is a materially
/// larger grant of authority, and it should be the result of an operator deciding to hand it out — not
/// of a consumer adopting a template version in which it happened to be on.
/// </para>
/// <para>
/// <strong>This section does not decide which tools a caller may invoke.</strong> That is the
/// caller's <c>CapabilityEnvelope</c>, resolved per credential, and nothing here widens it — with one
/// deliberate exception: <see cref="McpEnabled"/> covers a narrower surface (tools reached through a
/// server the operator already connected this host to) and is on by default. See its own remarks.
/// What this section bounds is the shape of a single invocation: how long it may run, how large its
/// arguments may be, and how much output it may return. A caller granted a tool can still only use it
/// within these.
/// </para>
/// <code>
/// AppConfig.AI.DirectToolInvocation
/// ├── Enabled              — Master toggle for keyed-DI tool invocation (default false)
/// ├── McpEnabled           — Master toggle for MCP tool invocation (default true)
/// ├── MaxRequestBytes      — Reject a request body larger than this, before deserialization
/// ├── InvocationTimeout    — Server ceiling on how long one invocation may run
/// ├── MaxOutputCharacters  — Truncate a tool's output beyond this before returning it
/// └── MaxParameterCount    — Maximum number of parameters one invocation may pass
/// </code>
/// </remarks>
public class DirectToolInvocationConfig
{
    /// <summary>
    /// Master toggle. When disabled (the default), the invocation endpoint refuses every request and
    /// the host behaves identically to one with no direct-invocation concept at all.
    /// </summary>
    /// <remarks>
    /// Read at request time through <c>IOptionsMonitor</c> rather than at registration, so the DI graph
    /// is identical in every host and an operator flipping this does not need a different container.
    /// The gate lives in the invoker, not only in the controller, so a future second caller of
    /// <c>IDirectToolInvoker</c> cannot reach the tool path around it.
    /// </remarks>
    /// <value>Default: false</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// Master toggle for the MCP-tool half of direct invocation (#481's <c>InvokeMcpToolAsync</c>),
    /// independent of <see cref="Enabled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On by default, unlike <see cref="Enabled"/>, and deliberately so.</strong> The two
    /// surfaces grant materially different authority. <see cref="Enabled"/> gates a caller naming one
    /// of the host's own first-party tools directly — file system access, sandboxed execution, whatever
    /// this deployment registers — which is exactly the "materially larger grant" <see cref="Enabled"/>'s
    /// own remarks describe. MCP invocation only reaches tools an operator already decided to trust
    /// enough to connect this host to, via <c>AppConfig.AI.McpServers</c>; running one of those tools
    /// from the WebUI's MCP panel is inspecting a connection the operator already made, not opening a
    /// new one. The panel is expected to work out of the box.
    /// </para>
    /// <para>
    /// Both surfaces still run every call through the identical admission/sanitize/redact/bound chain —
    /// this flag and <see cref="Enabled"/> gate whether a caller can reach that chain at all, never what
    /// the chain itself does once reached.
    /// </para>
    /// </remarks>
    /// <value>Default: true</value>
    public bool McpEnabled { get; set; } = true;

    /// <summary>
    /// Maximum accepted size, in bytes, of an invocation request body. Enforced before deserialization,
    /// so a hostile body costs a length check rather than a parse. Must be positive.
    /// </summary>
    /// <value>Default: 65536 (64 KiB)</value>
    public int MaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Server ceiling on how long a single invocation may run before it is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the control that makes a synchronous surface safe to expose. A tool that blocks
    /// indefinitely holds a request thread, a DI scope, and whatever the tool itself acquired, and the
    /// caller learns nothing until their own client gives up — by which point the host is still working.
    /// </para>
    /// <para>
    /// <strong>It is a ceiling, not a default a caller may raise.</strong> A caller may ask for less;
    /// asking for more is refused rather than clamped, matching how every <c>Max…</c> ceiling on the
    /// workflow-submission surface behaves. Silently lowering a requested budget produces a timeout the
    /// caller cannot explain from anything in the response.
    /// </para>
    /// </remarks>
    /// <value>Default: 30 seconds</value>
    public TimeSpan InvocationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of characters of tool output returned to the caller. Output beyond this is
    /// truncated, and the response says so. Must be positive.
    /// </summary>
    /// <remarks>
    /// Truncation here is a response-size bound, and is deliberately <em>not</em> the harness's
    /// tool-output compression. Compression exists to fit a result into a model's context window and
    /// does so by summarising and by replacing content with pointers the agent can expand — pointers a
    /// caller on the other side of an HTTP boundary has no way to follow. A caller is better served by
    /// a truthful prefix that says it was cut than by a summary of something they cannot retrieve.
    /// </remarks>
    /// <value>Default: 262144 (256 Ki characters)</value>
    public int MaxOutputCharacters { get; set; } = 256 * 1024;

    /// <summary>
    /// Maximum number of parameters a single invocation may pass to a tool. Must be positive.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxRequestBytes"/> bounds the body's total size but not its shape: a body well under
    /// the byte ceiling can still carry many thousands of one-character keys, and the cost of handling
    /// those falls on the tool rather than on the parse. Bounding the count directly is the cheaper and
    /// more predictable of the two checks.
    /// </remarks>
    /// <value>Default: 64</value>
    public int MaxParameterCount { get; set; } = 64;
}
