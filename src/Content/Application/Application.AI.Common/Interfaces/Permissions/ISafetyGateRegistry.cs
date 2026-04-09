using Domain.AI.Permissions;

namespace Application.AI.Common.Interfaces.Permissions;

/// <summary>
/// Registry of safety gates -- paths and operations that always require explicit approval.
/// Safety gates are bypass-immune and checked before any allow rules.
/// </summary>
public interface ISafetyGateRegistry
{
    /// <summary>Gets all registered safety gates.</summary>
    IReadOnlyList<SafetyGate> Gates { get; }

    /// <summary>
    /// Checks whether any safety gate is triggered by the given tool parameters.
    /// </summary>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="parameters">The tool's execution parameters (may contain file paths, etc.).</param>
    /// <returns>The triggered gate if any, or null if no gate is triggered.</returns>
    SafetyGate? CheckSafetyGate(
        string toolName,
        IReadOnlyDictionary<string, object?>? parameters);
}
