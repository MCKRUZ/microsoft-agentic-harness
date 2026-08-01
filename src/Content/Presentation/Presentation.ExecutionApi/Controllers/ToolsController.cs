using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.Common.Extensions;
using Presentation.ExecutionApi.DTOs;
using Presentation.ExecutionApi.Extensions;
using Presentation.ExecutionApi.Services;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// Discovery for the tools the calling credential may invoke in this host, and — where an operator has
/// opted in — invocation of one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two surfaces, gated separately and deliberately so.</strong> The <c>GET</c> actions require
/// only authentication, because listing a tool confers nothing. <see cref="Invoke"/> additionally
/// requires <see cref="InvokeRole"/> and an operator having enabled
/// <c>AppConfig:AI:DirectToolInvocation</c>, because it executes host-side code on the host's own
/// resources at a caller's request. Splitting the claims is what lets an operator issue a credential
/// that can see the catalog but not act on it.
/// </para>
/// <para>
/// <strong>What this is for.</strong> A workflow's <c>ToolUse</c> step names a tool and an operation,
/// and until now there was no way for an external author to find out which names this host accepts
/// short of reading its source. Every answer here is scoped to the caller's own capability envelope,
/// so the listing doubles as the authoritative answer to "what am I allowed to do".
/// </para>
/// <para>
/// <strong>This is not the MCP tool listing.</strong> <c>/api/mcp/tools</c> on AgentHub enumerates
/// tools published by external MCP servers the host has connected to; this enumerates the tools the
/// host itself registers. The two sets are disjoint in origin and are governed differently — an MCP
/// tool additionally requires its server to be granted by
/// <c>CapabilityEnvelope.AllowedMcpServers</c>.
/// </para>
/// <para>
/// <strong>Discovery confers nothing.</strong> Listing a tool is not permission to invoke it and does
/// not arm anything; the envelope this filters by is the same one the governor enforces at
/// invocation. The endpoint is deliberately separate from invocation so a consumer can attach a
/// different policy to each.
/// </para>
/// </remarks>
[ApiController]
[Route("api/tools")]
[Authorize]
[EnableRateLimiting(ExecutionApiServiceCollectionExtensions.DefaultRateLimitPolicy)]
public sealed class ToolsController : ControllerBase
{
    /// <summary>
    /// The role a caller must hold to run a tool through <see cref="Invoke"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrower than the controller's own <c>[Authorize]</c>, which is all discovery
    /// requires. Reading the catalog tells a caller what the host could do for them; invoking makes it
    /// happen, on the host's own credentials and against the host's own resources. Those are different
    /// grants and an operator should be able to hand out the first without the second — which is only
    /// possible if they are separate claims.
    /// </para>
    /// <para>
    /// Note the asymmetry with the rest of this host: bundle and workflow runs execute an <em>agent</em>
    /// that decides for itself which tools to use, and are gated by the envelope alone. Here the caller
    /// names the tool and the operation directly, so the role sits on top of the envelope rather than
    /// in place of it.
    /// </para>
    /// </remarks>
    public const string InvokeRole = "Harness.Tools.Invoke";

    private readonly IToolCatalog _catalog;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;
    private readonly IDirectToolInvoker _invoker;

    /// <summary>Initializes the controller with the catalog, the envelope resolver, and the invoker.</summary>
    public ToolsController(
        IToolCatalog catalog,
        ICapabilityEnvelopeResolver envelopeResolver,
        IDirectToolInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(envelopeResolver);
        ArgumentNullException.ThrowIfNull(invoker);

        _catalog = catalog;
        _envelopeResolver = envelopeResolver;
        _invoker = invoker;
    }

    /// <summary>
    /// Lists the tools this caller may invoke, ordered by name.
    /// </summary>
    /// <remarks>
    /// An empty list is a valid, successful answer: the shipped default envelope grants no tools, so
    /// a host whose operator has configured no grants answers 200 with nothing in it. That is a
    /// configuration statement, not an error, and answering 403 instead would wrongly suggest the
    /// caller's credential is at fault.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ToolCatalogResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult List()
    {
        var envelope = _envelopeResolver.Resolve(User);

        return Ok(new ToolCatalogResponse
        {
            Tools = [.. _catalog.ListGranted(envelope).Select(ToEntry)]
        });
    }

    /// <summary>
    /// Describes a single tool this caller may invoke.
    /// </summary>
    /// <param name="name">The tool name, matched case-insensitively.</param>
    /// <remarks>
    /// A tool the caller is not granted answers <c>404</c>, identically to one that does not exist.
    /// This mirrors the harness's standing rule that an authorization mismatch reads as absence rather
    /// than refusal: a <c>403</c> here would confirm the tool exists, letting any authenticated caller
    /// map the host's full inventory one name at a time — the exact disclosure the envelope filter on
    /// the list endpoint prevents.
    /// </remarks>
    [HttpGet("{name}")]
    [ProducesResponseType(typeof(ToolCatalogEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(string name)
    {
        var envelope = _envelopeResolver.Resolve(User);
        var descriptor = _catalog.FindGranted(name, envelope);

        return descriptor is null
            ? NotFound()
            : Ok(ToEntry(descriptor));
    }

    /// <summary>
    /// Runs one operation of one tool and returns its result.
    /// </summary>
    /// <param name="name">The tool to run, matched case-insensitively.</param>
    /// <param name="request">The operation and its arguments.</param>
    /// <param name="cancellationToken">Cancelled when the caller disconnects.</param>
    /// <remarks>
    /// <para>
    /// <strong>This executes host-side code on a caller's say-so, which is why it is gated three
    /// times.</strong> The caller must authenticate; must hold <see cref="InvokeRole"/>; and the tool
    /// must be granted by the capability envelope their credential resolves to. The first two are
    /// checked here; the third is enforced inside <see cref="IDirectToolInvoker"/>, both by the catalog
    /// lookup and independently by the invocation governor — which arming the envelope switches on.
    /// </para>
    /// <para>
    /// <strong>The status codes carry meaning worth honouring.</strong> A tool the caller is not
    /// granted, one that does not exist, and one not offered on this surface are all <c>404</c>: any
    /// other answer would let a caller map the host's inventory one name at a time. <c>403</c> means
    /// governance refused an invocation of a tool the caller <em>is</em> granted — an autonomy ceiling
    /// or a policy rule — or that the host has direct invocation switched off. <c>200</c> with
    /// <c>succeeded: false</c> is a tool that ran and said no, which is a different event from any of
    /// these and must not be conflated with them.
    /// </para>
    /// <para>
    /// <strong>Output is sanitized and bounded, never compressed.</strong> The harness's tool-output
    /// compression exists to fit results into a model's context window and does it by summarising and
    /// substituting pointers an agent can expand; a caller on the far side of HTTP cannot expand them,
    /// so they would receive a reference instead of an answer.
    /// </para>
    /// </remarks>
    [HttpPost("{name}/invoke")]
    [Authorize(Roles = InvokeRole)]
    [EnableRateLimiting(ExecutionApiServiceCollectionExtensions.InvokeRateLimitPolicy)]
    [ServiceFilter(typeof(ToolInvocationRequestSizeLimitFilter))]
    [ProducesResponseType(typeof(ToolInvocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Invoke(
        string name,
        [FromBody] ToolInvocationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Problem(
                title: "Validation failed",
                detail: "A request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Identity from the token, never the body. A null id means the credential carries nothing
        // durable to attribute the invocation to, which is a refusal rather than a shared bucket.
        var ownerId = BundleCallerIdentity.StableId(User);
        if (string.IsNullOrEmpty(ownerId))
        {
            return this.NoUsableIdentity();
        }

        var operation = request.Operation ?? string.Empty;

        var outcome = await _invoker.InvokeAsync(
            new DirectToolInvocationRequest
            {
                ToolName = name,
                Operation = operation,
                Parameters = ToolParameters.FromJson(request.Parameters),
                OwnerId = ownerId,
                Envelope = _envelopeResolver.Resolve(User),
                RequestedTimeout = request.TimeoutSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null
            },
            cancellationToken);

        return outcome.Status switch
        {
            DirectToolInvocationStatus.Succeeded or DirectToolInvocationStatus.ToolFailed =>
                Ok(ToResponse(name, operation, outcome)),

            // Absent, ungranted, and not-offered-here are one answer. Anything that distinguished them
            // would let a caller map the host's inventory one name at a time.
            DirectToolInvocationStatus.NotFound => NotFound(),

            // Same answer as an absent identity, because it is the same problem with the same remedy:
            // the credential cannot own work here, so the caller needs a different token.
            DirectToolInvocationStatus.IdentityUnusable => this.NoUsableIdentity(),

            DirectToolInvocationStatus.Invalid => Problem(
                title: "Validation failed", detail: outcome.Error,
                statusCode: StatusCodes.Status400BadRequest),

            // Forbid() rather than a Problem body. The two 403 causes — governance refused this
            // invocation, and the host has the surface switched off — are deliberately not told apart:
            // a caller learning which one applied learns whether they would be permitted if it were on.
            DirectToolInvocationStatus.Denied or DirectToolInvocationStatus.Disabled => Forbid(),

            DirectToolInvocationStatus.TimedOut => Problem(
                title: "Tool timed out", detail: outcome.Error,
                statusCode: StatusCodes.Status504GatewayTimeout),

            _ => Problem(
                title: "Tool invocation failed", detail: outcome.Error,
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static ToolInvocationResponse ToResponse(
        string name, string operation, DirectToolInvocationOutcome outcome) =>
        new()
        {
            Tool = name,
            Operation = operation,
            Succeeded = outcome.Status == DirectToolInvocationStatus.Succeeded,
            Output = outcome.Output,
            Error = outcome.Error,
            OutputTruncated = outcome.OutputTruncated,
            DurationMs = (long)outcome.Duration.TotalMilliseconds
        };

    private static ToolCatalogEntry ToEntry(ToolDescriptor descriptor) =>
        new()
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Operations = descriptor.SupportedOperations,
            RiskTier = descriptor.Risk.Radius,
            IsReadOnly = descriptor.Risk.IsReadOnly,
            IsConcurrencySafe = descriptor.IsConcurrencySafe,
            IsDirectlyInvocable = descriptor.IsDirectlyInvocable
        };
}
