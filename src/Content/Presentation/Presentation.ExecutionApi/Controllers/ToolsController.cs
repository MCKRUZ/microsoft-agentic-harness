using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Presentation.ExecutionApi.DTOs;
using Presentation.ExecutionApi.Extensions;

namespace Presentation.ExecutionApi.Controllers;

/// <summary>
/// Read-only discovery for the tools the calling credential may invoke in this host.
/// </summary>
/// <remarks>
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
    private readonly IToolCatalog _catalog;
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;

    /// <summary>Initializes the controller with the catalog and the caller's envelope resolver.</summary>
    public ToolsController(IToolCatalog catalog, ICapabilityEnvelopeResolver envelopeResolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(envelopeResolver);

        _catalog = catalog;
        _envelopeResolver = envelopeResolver;
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

    private static ToolCatalogEntry ToEntry(ToolDescriptor descriptor) =>
        new()
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Operations = descriptor.SupportedOperations,
            RiskTier = descriptor.Risk.Radius,
            IsReadOnly = descriptor.Risk.IsReadOnly,
            IsConcurrencySafe = descriptor.IsConcurrencySafe
        };
}
