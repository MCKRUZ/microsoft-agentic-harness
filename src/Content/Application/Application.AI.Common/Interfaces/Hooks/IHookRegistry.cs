using Domain.AI.Hooks;

namespace Application.AI.Common.Interfaces.Hooks;

/// <summary>
/// Registry for hook definitions. Hooks are registered at startup or dynamically
/// and matched against events during execution.
/// </summary>
public interface IHookRegistry
{
    /// <summary>Registers a hook definition.</summary>
    /// <param name="hook">The hook definition to register.</param>
    void Register(HookDefinition hook);

    /// <summary>Unregisters a hook by its ID.</summary>
    /// <param name="hookId">The unique identifier of the hook to remove.</param>
    /// <returns><c>true</c> if the hook was found and removed; <c>false</c> otherwise.</returns>
    bool Unregister(string hookId);

    /// <summary>
    /// Gets all hooks matching the specified event and optional tool name.
    /// Results are ordered by priority (ascending).
    /// </summary>
    /// <param name="hookEvent">The lifecycle event to match.</param>
    /// <param name="toolName">
    /// Optional tool name to match against hook <see cref="HookDefinition.ToolMatcher"/> patterns.
    /// Only relevant for tool lifecycle events.
    /// </param>
    /// <returns>An ordered list of matching hook definitions.</returns>
    IReadOnlyList<HookDefinition> GetHooksForEvent(HookEvent hookEvent, string? toolName = null);
}
