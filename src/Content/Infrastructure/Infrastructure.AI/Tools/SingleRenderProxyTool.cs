using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Base class for the single-operation "render" generative-UI tools — <see cref="RenderChartTool"/>,
/// <see cref="RenderImageTool"/>, <see cref="RenderFormTool"/>, and <see cref="RenderTableTool"/>. Each
/// summons one widget in the connected browser via the shared blocking-proxy round-trip and returns a
/// short acknowledgement; they differ only in their name, description, argument validation, and the two
/// user-facing failure messages. This base owns the identical execution scaffolding they would otherwise
/// each duplicate: the <c>render</c>-operation gate, the client-attached check, the argument
/// serialization, and the validate-then-invoke sequence.
/// </summary>
/// <remarks>
/// Multi-operation proxy tools such as <see cref="DashboardControlTool"/> do <b>not</b> derive from this
/// class — they extend <see cref="BlockingProxyTool"/> directly because their dispatch is not a single
/// fixed operation. A subclass here supplies <see cref="NoClientMessage"/>, <see cref="TimeoutMessage"/>,
/// and (optionally) an <see cref="ValidateArguments"/> override; everything else is inherited.
/// </remarks>
public abstract class SingleRenderProxyTool : BlockingProxyTool
{
    /// <summary>The single operation every render tool supports.</summary>
    protected const string RenderOperation = "render";

    private static readonly IReadOnlyList<string> RenderOperations = [RenderOperation];

    /// <summary>Initializes the base with the client round-trip bridge.</summary>
    /// <param name="bridge">The bridge used to delegate rendering to the browser.</param>
    protected SingleRenderProxyTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public sealed override IReadOnlyList<string> SupportedOperations => RenderOperations;

    /// <summary>The failure message returned when no client is attached to service the render.</summary>
    protected abstract string NoClientMessage { get; }

    /// <summary>The failure message returned when the client does not render within the bounded timeout.</summary>
    protected abstract string TimeoutMessage { get; }

    /// <summary>
    /// Validates the render arguments; returns an error message, or null when valid. Both the raw
    /// parameter dictionary and its serialized JSON are supplied so a subclass validates from whichever
    /// is more convenient (a top-level scalar from the dictionary, nested structure from the JSON)
    /// without re-serializing. The default accepts everything.
    /// </summary>
    protected virtual string? ValidateArguments(
        IReadOnlyDictionary<string, object?> parameters, string argumentsJson) => null;

    /// <inheritdoc />
    public sealed override Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, RenderOperation, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail($"Unknown operation: {operation}. Supported: {RenderOperation}"));

        if (!IsClientAttached)
            return Task.FromResult(ToolResult.Fail(NoClientMessage));

        var argumentsJson = JsonSerializer.Serialize(parameters, SerializerOptions);

        var validationError = ValidateArguments(parameters, argumentsJson);
        if (validationError is not null)
            return Task.FromResult(ToolResult.Fail(validationError));

        return InvokeClientAsync(argumentsJson, TimeoutMessage, cancellationToken);
    }
}
