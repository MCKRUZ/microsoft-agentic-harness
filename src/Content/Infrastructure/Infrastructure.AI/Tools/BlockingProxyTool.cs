using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Base class for "blocking proxy" tools — server-side tools whose effect is carried out by the
/// connected client mid-run. A subclass validates its operation and shapes a JSON argument payload;
/// this base owns the shared round-trip plumbing: the client-attached check, JSON options, and the
/// mapping of an <see cref="IClientToolBridge"/> call to a <see cref="ToolResult"/>.
/// </summary>
/// <remarks>
/// The bridge emits the AG-UI <c>TOOL_CALL_*</c> events, blocks awaiting the browser's reply, and
/// resumes the same run with it (see <c>AgUiClientToolBridge</c>). Subclasses such as
/// <c>DashboardControlTool</c> and <c>RenderChartTool</c> differ only in their operation set and the
/// shape of the payload they send.
/// </remarks>
public abstract class BlockingProxyTool : ITool
{
    /// <summary>Shared serializer options for the argument payload sent to the client.</summary>
    protected static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IClientToolBridge _bridge;

    /// <summary>Initializes the base with the client round-trip bridge.</summary>
    /// <param name="bridge">The bridge used to delegate the operation to the browser.</param>
    protected BlockingProxyTool(IClientToolBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> SupportedOperations { get; }

    /// <summary>Client round-trip tools read/act on the live view; low intrinsic blast radius.</summary>
    public virtual BlastRadius RiskTier => BlastRadius.Low;

    // IsReadOnly / IsConcurrencySafe intentionally keep the fail-closed defaults (false): the
    // concurrency classifier then serializes these view-mutating proxy calls, which is the desired
    // behaviour (two simultaneous navigations would race). AgUiEventWriter additionally serializes
    // frame writes to defend against the framework's own concurrent function invocation.

    /// <summary>
    /// Never directly invocable over HTTP. Every tool in this hierarchy carries out its effect through
    /// a browser attached to the <em>current run</em>, so the whole family is excluded here rather than
    /// tool by tool — the unsuitability is a property of the round trip, not of any one subclass.
    /// </summary>
    /// <remarks>
    /// A direct HTTP caller has no attached client, so the best case is that the tool reports one is
    /// missing and the caller has spent a request to be told so. The worse case is a host that does
    /// have a browser attached: the call then succeeds by driving <em>somebody else's</em> live view,
    /// and returns to the HTTP caller a reference to a widget rendered in a session they are not part
    /// of. Neither outcome is something a caller could act on, so the surface should not offer it.
    /// </remarks>
    public bool IsDirectlyInvocable => false;

    // No RequiredCapabilities override for this family (RenderChartTool, RenderImageTool,
    // RenderFormTool, RenderTableTool, DashboardControlTool): the whole effect is an in-process
    // AG-UI round-trip to a browser already attached to this run — no filesystem, subprocess, or new
    // outbound network connection this tool itself initiates. ToolCapability.None (the interface
    // default) is the honest answer, not an oversight (#387).

    /// <summary>Whether a client is currently attached and able to service the round-trip.</summary>
    protected bool IsClientAttached => _bridge.IsClientAttached;

    /// <summary>
    /// Delegates <paramref name="argumentsJson"/> to the connected client and maps the outcome to a
    /// <see cref="ToolResult"/>: success → <see cref="ToolResult.Ok"/>; a bounded-timeout → a failure
    /// carrying <paramref name="timeoutMessage"/>; a no-client race (the bridge throwing
    /// <see cref="InvalidOperationException"/>) → a failure carrying that message.
    /// <see cref="OperationCanceledException"/> is intentionally allowed to propagate so a cancelled
    /// run unwinds rather than being reported as a benign tool failure.
    /// </summary>
    protected async Task<ToolResult> InvokeClientAsync(
        string argumentsJson, string timeoutMessage, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _bridge.InvokeAsync(Name, argumentsJson, cancellationToken);
            return ToolResult.Ok(result);
        }
        catch (TimeoutException)
        {
            return ToolResult.Fail(timeoutMessage);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    /// <inheritdoc />
    public abstract Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}
