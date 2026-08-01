using Domain.AI.Bundles;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Executes one of the host's registered tools synchronously on behalf of an external caller, under the
/// same governance the agent's own tool calls run under.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the single arming site for direct invocation.</strong> Running a tool safely means
/// establishing several ambient facts in a fixed order — the caller's identity, then their capability
/// envelope, then the governance accessors — and every one of them must be torn down afterwards. Spread
/// across a controller and a handler that sequence is a thing each new call site has to get right; here
/// it exists once. A second caller of this interface inherits the discipline instead of re-deriving it,
/// which is the same doctrine <c>IBundleRunExecutor</c> and <c>IPlanRunExecutor</c> follow.
/// </para>
/// <para>
/// <strong>It grants nothing.</strong> The envelope on the request is the authority, resolved from the
/// caller's credential by <c>ICapabilityEnvelopeResolver</c> at the transport boundary. This type
/// consumes that grant and cannot widen it: an ungranted tool is refused by the catalog before
/// execution and again by <c>IToolInvocationGovernor</c>, which arming the envelope switches on
/// automatically.
/// </para>
/// <para>
/// <strong>Feature-gating lives here, not only in the controller.</strong> A host with direct invocation
/// disabled refuses at this boundary, so the gate cannot be bypassed by a future in-process caller that
/// does not go through HTTP.
/// </para>
/// </remarks>
public interface IDirectToolInvoker
{
    /// <summary>
    /// Invokes a tool and returns its sanitized result.
    /// </summary>
    /// <param name="request">What to run, for whom, and under which grant.</param>
    /// <param name="cancellationToken">Cancels the invocation; the caller disconnecting cancels it too.</param>
    /// <returns>
    /// The outcome, always. Every anticipated failure — disabled host, malformed request, ungranted or
    /// unsuitable tool, governance denial, timeout, a tool that reported failure — is a
    /// <see cref="DirectToolInvocationStatus"/> rather than an exception or a failed <c>Result</c>.
    /// </returns>
    /// <remarks>
    /// <strong>Why an outcome status rather than the harness's usual <c>Result&lt;T&gt;</c>.</strong>
    /// The caller has to tell these failures apart to answer correctly — an ungranted tool and a
    /// governance denial and a timeout are three different HTTP responses — and <c>Result</c> carries
    /// its failures as strings. Distinguishing them would mean matching on message text at the
    /// transport boundary, which is precisely the coupling that makes an error message impossible to
    /// reword later. The status is the contract; the message is for humans.
    /// </remarks>
    Task<DirectToolInvocationOutcome> InvokeAsync(
        DirectToolInvocationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A request to run one tool operation on behalf of one caller.
/// </summary>
public sealed record DirectToolInvocationRequest
{
    /// <summary>The tool to run, matched case-insensitively against the registration key.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The operation to perform. Must be one the tool declares in <see cref="ITool.SupportedOperations"/>;
    /// the catalog publishes that list so a caller can get this right before spending a request.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>The operation's parameters. Empty is valid — several operations take none.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// The caller's stable identifier, resolved from their token at the transport boundary and never
    /// accepted from the request body.
    /// </summary>
    /// <remarks>
    /// This becomes the permission-resolution subject and the audit subject for the invocation, so it
    /// is the one field whose provenance decides whether the governance trace means anything. A caller
    /// able to supply it could attribute their tool use to somebody else.
    /// </remarks>
    public required string OwnerId { get; init; }

    /// <summary>
    /// The capability envelope the host grants this caller — the authoritative allowlist of tools they
    /// may invoke.
    /// </summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>
    /// An optional shorter deadline for this invocation. Null means the configured ceiling.
    /// </summary>
    /// <remarks>
    /// A caller may ask for less time than the host's ceiling; asking for more is refused rather than
    /// silently clamped, matching how every admission ceiling on the workflow surface behaves. A caller
    /// who requested ten minutes and was quietly given thirty seconds would experience an unexplainable
    /// timeout, with nothing in the response to attribute it to.
    /// </remarks>
    public TimeSpan? RequestedTimeout { get; init; }
}

/// <summary>
/// How an invocation ended.
/// </summary>
/// <remarks>
/// <strong>These are not one-to-one with HTTP statuses, deliberately.</strong>
/// <see cref="Succeeded"/> and <see cref="ToolFailed"/> both answer <c>200</c> — the status describes
/// the invocation, and a tool that ran and said no is a completed invocation. <see cref="Denied"/> and
/// <see cref="Disabled"/> both answer <c>403</c> with no body, because a caller who could tell them
/// apart would learn whether they <em>would</em> be permitted if the operator switched the surface on.
/// The distinctions this enum draws are for the host's own logic and logs; the transport collapses
/// some of them on purpose.
/// </remarks>
public enum DirectToolInvocationStatus
{
    /// <summary>The tool ran and reported success. <see cref="DirectToolInvocationOutcome.Output"/> holds its result.</summary>
    Succeeded = 0,

    /// <summary>
    /// The tool ran and reported failure. This is a successful invocation of a tool that said no —
    /// distinct from every other value here, all of which mean the tool never executed.
    /// </summary>
    ToolFailed = 1,

    /// <summary>
    /// The tool does not exist, is not granted by the caller's envelope, or is not offered on the
    /// direct-invocation surface. The three are deliberately one value: telling a caller which of them
    /// applies would let them map the host's tool inventory one name at a time.
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The caller's envelope grants the tool but governance refused this invocation — an autonomy
    /// ceiling, a policy rule, or a capability the host does not grant.
    /// </summary>
    Denied = 3,

    /// <summary>The invocation exceeded its deadline and was cancelled.</summary>
    TimedOut = 4,

    /// <summary>The request was malformed: an unknown operation, too many parameters, or an unusable identity.</summary>
    Invalid = 5,

    /// <summary>Direct invocation is not enabled on this host.</summary>
    Disabled = 6,

    /// <summary>The invocation threw. The detail is in the host's logs, never in the response.</summary>
    Faulted = 7
}

/// <summary>
/// The result of an invocation attempt.
/// </summary>
public sealed record DirectToolInvocationOutcome
{
    /// <summary>How the invocation ended.</summary>
    public required DirectToolInvocationStatus Status { get; init; }

    /// <summary>
    /// The tool's output on success, already sanitized and truncated to the configured ceiling. Null
    /// for every non-success status.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// A caller-safe explanation of a non-success status, or a failing tool's own message.
    /// </summary>
    /// <remarks>
    /// Sanitized on the same path as the output, and never raw exception text: tool failures routinely
    /// carry filesystem paths, connection strings, and container internals, and this crosses a trust
    /// boundary. A <see cref="DirectToolInvocationStatus.Faulted"/> outcome carries a stable code only —
    /// the exception itself is logged.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>
    /// Whether the output was cut short at the configured character ceiling. A caller that cannot tell
    /// a complete result from a prefix of one will parse the prefix as complete.
    /// </summary>
    public bool OutputTruncated { get; init; }

    /// <summary>How long the invocation took, measured around the tool call itself.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Builds an outcome for a status that carries no tool output.</summary>
    /// <param name="status">The terminal status.</param>
    /// <param name="error">The caller-safe explanation.</param>
    /// <param name="duration">Elapsed time, where it is meaningful.</param>
    public static DirectToolInvocationOutcome Refused(
        DirectToolInvocationStatus status, string error, TimeSpan duration = default) =>
        new() { Status = status, Error = error, Duration = duration };
}
