using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Permissions;

/// <summary>
/// Default implementation of <see cref="ISafetyGateRegistry"/> that loads safety gate
/// paths from <see cref="AppConfig"/> configuration. Safety gates represent paths and
/// operations that always require explicit user approval regardless of other permission rules.
/// </summary>
public sealed class SafetyGateRegistry : ISafetyGateRegistry
{
    private static readonly string[] PathParameterKeys =
        ["path", "file_path", "directory", "filePath", "dir", "target"];

    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private IReadOnlyList<SafetyGate>? _cachedGates;
    private IReadOnlyList<string>? _cachedPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="SafetyGateRegistry"/> class.
    /// </summary>
    /// <param name="appConfig">Application configuration providing safety gate paths.</param>
    public SafetyGateRegistry(IOptionsMonitor<AppConfig> appConfig)
    {
        _appConfig = appConfig;
    }

    /// <inheritdoc />
    public IReadOnlyList<SafetyGate> Gates
    {
        get
        {
            var currentPaths = _appConfig.CurrentValue.AI.Permissions.SafetyGatePaths;
            if (_cachedGates is not null && ReferenceEquals(_cachedPaths, currentPaths))
                return _cachedGates;

            _cachedPaths = currentPaths;
            _cachedGates = currentPaths
                .Select(path => new SafetyGate(path, $"Safety gate for protected path: {path}"))
                .ToList();
            return _cachedGates;
        }
    }

    /// <inheritdoc />
    public SafetyGate? CheckSafetyGate(
        string toolName,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        var gates = Gates;
        foreach (var key in PathParameterKeys)
        {
            if (!parameters.TryGetValue(key, out var paramValue) || paramValue is null)
                continue;

            var pathValue = paramValue.ToString();
            if (string.IsNullOrEmpty(pathValue))
                continue;

            foreach (var gate in gates)
            {
                if (PathContainsGatePattern(pathValue, gate.PathPattern))
                    return gate;
            }
        }

        return null;
    }

    private static bool PathContainsGatePattern(string path, string gatePattern)
    {
        var normalizedPath = path.Replace('\\', '/');
        var normalizedGate = gatePattern.Replace('\\', '/');

        return normalizedPath.Contains(normalizedGate, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedGate, StringComparison.OrdinalIgnoreCase);
    }
}
