using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Iac;
using Domain.AI.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools.Iac;

/// <summary>
/// Scaffolds a starter IaC module via the backend's <c>IIacGenerator</c> and
/// returns the generated files as JSON. The agent refines the scaffold and submits
/// it as a <c>ChangeProposal</c>; this tool only produces files — it never writes
/// to disk or the cluster, so it is treated as read-only.
/// </summary>
public sealed class IacGenerateTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "iac_generate";

    private static readonly IReadOnlyList<string> Operations = ["generate"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initialises a new <see cref="IacGenerateTool"/>.</summary>
    /// <param name="services">Service provider for keyed backend resolution.</param>
    /// <param name="config">Application configuration monitor — supplies the default backend.</param>
    public IacGenerateTool(IServiceProvider services, IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        _services = services;
        _config = config;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Scaffolds a starter IaC module (Terraform or Bicep) for a resource type and name. Returns the generated files as JSON; does not write to disk or deploy.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "generate", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: generate.");

        var resolved = IacToolBackendResolver.Resolve(_services, _config, parameters);
        if (!resolved.IsSuccess || resolved.Value is null)
            return ToolResult.Fail(string.Join("; ", resolved.Errors));

        if (!TryReadRequired(parameters, "resource_type", out var resourceType))
            return ToolResult.Fail("Required parameter 'resource_type' is missing or empty.");
        if (!TryReadRequired(parameters, "resource_name", out var resourceName))
            return ToolResult.Fail("Required parameter 'resource_name' is missing or empty.");

        var request = new IacGenerationRequest
        {
            Backend = resolved.Value.Backend,
            ResourceType = resourceType,
            ResourceName = resourceName,
            Environment = ReadOptional(parameters, "environment", "dev"),
            Parameters = ReadParameterMap(parameters)
        };

        var result = await resolved.Value.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return ToolResult.Fail(string.Join("; ", result.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(result.Value, SerializerOptions));
    }

    private static bool TryReadRequired(IReadOnlyDictionary<string, object?> parameters, string key, out string value)
    {
        value = string.Empty;
        if (parameters.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
        {
            value = s.Trim();
            return true;
        }

        return false;
    }

    private static string ReadOptional(IReadOnlyDictionary<string, object?> parameters, string key, string fallback)
        => parameters.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : fallback;

    private static IReadOnlyDictionary<string, string> ReadParameterMap(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("parameters", out var raw)
            && raw is IReadOnlyDictionary<string, object?> map)
        {
            return map
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!.ToString() ?? string.Empty);
        }

        return new Dictionary<string, string>();
    }
}
