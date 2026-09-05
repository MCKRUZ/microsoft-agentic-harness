using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Pure passthrough wrapper that carries an <see cref="ITool"/>'s
/// <see cref="Application.AI.Common.Interfaces.Tools.ITool.ResourceParametersByOperation"/>
/// declaration forward onto the converted <see cref="AIFunction"/>, so
/// <see cref="GovernedAIFunction"/> can find it regardless of which <c>IToolConverter</c> produced
/// the function (#418).
/// </summary>
/// <remarks>
/// <para>
/// Applied once, at <c>ToolChainBuilder.ResolveToolByName</c> — the one place a raw <see cref="ITool"/>
/// and its just-converted <see cref="AIFunction"/> are both in hand. Converter-agnostic by design:
/// it wraps whatever <see cref="AIFunction"/> a converter produced rather than requiring the
/// converter itself to know about resource-parameter declarations, so a future tool-specific
/// (priority-100) converter gets this for free.
/// </para>
/// <para>
/// Survives <c>ToolChainBuilder.ApplyCompositionTaint</c>'s later re-wrap unchanged: that re-wrap
/// constructs a new <c>GovernedAIFunction</c> around the existing <c>GovernedAIFunction.Inner</c> —
/// the same instance of this class — so the map is never lost or duplicated across that step. Never
/// collides with <see cref="McpFailureNormalizingAIFunction"/>: MCP-sourced tools never flow through
/// <c>ResolveToolByName</c>, the only place this wrapper is ever applied.
/// </para>
/// </remarks>
internal sealed class ResourceParameterDeclaringAIFunction : DelegatingAIFunction
{
    /// <summary>The wrapped tool's per-operation resource-parameter declaration.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> ResourceParametersByOperation { get; }

    /// <param name="innerFunction">The already-converted function to wrap.</param>
    /// <param name="resourceParametersByOperation">
    /// The declaring tool's <c>ResourceParametersByOperation</c> — must be non-empty; the caller
    /// (<c>ToolChainBuilder</c>) only wraps when the tool actually declares something.
    /// </param>
    public ResourceParameterDeclaringAIFunction(
        AIFunction innerFunction,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> resourceParametersByOperation)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(resourceParametersByOperation);
        ResourceParametersByOperation = resourceParametersByOperation;
    }
}
