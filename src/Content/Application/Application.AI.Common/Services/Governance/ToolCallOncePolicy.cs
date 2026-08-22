using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCallOncePolicy"/>: a concurrent set of tool names, written once per
/// resolved declaration and read on every admission check.
/// </summary>
/// <remarks>
/// Registered as a singleton — the set must outlive the tool-resolution scope that populated it,
/// since the admission check happens later, on a different scope, matching
/// <c>ToolBehaviorRegistry</c>'s lifetime reasoning.
/// </remarks>
public sealed class ToolCallOncePolicy : IToolCallOncePolicy
{
    private readonly ConcurrentDictionary<string, byte> _callOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        _callOnce.TryAdd(toolName, 0);
    }

    /// <inheritdoc />
    public bool IsCallOnce(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _callOnce.ContainsKey(toolName);
}
