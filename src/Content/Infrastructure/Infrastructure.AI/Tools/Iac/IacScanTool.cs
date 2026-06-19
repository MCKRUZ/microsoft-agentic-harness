using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools.Iac;

/// <summary>
/// Security-scans an IaC module via the backend's <c>IIacGenerator</c> (Checkov +
/// tfsec for Terraform, ARM-TTK + Checkov for Bicep), returning normalised findings
/// and the pass/fail verdict as JSON. Read-only and concurrency-safe.
/// </summary>
public sealed class IacScanTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "iac_scan";

    private static readonly IReadOnlyList<string> Operations = ["scan"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;

    /// <summary>Initialises a new <see cref="IacScanTool"/>.</summary>
    /// <param name="services">Service provider for keyed backend resolution.</param>
    /// <param name="config">Application configuration monitor — supplies the default backend.</param>
    public IacScanTool(IServiceProvider services, IOptionsMonitor<AppConfig> config)
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
        "Security-scans an IaC module (Checkov + tfsec / ARM-TTK + Checkov) inside the sandbox. Returns normalised findings and a pass/fail verdict as JSON. Read-only.";

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
        if (!string.Equals(operation, "scan", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: scan.");

        var resolved = IacToolBackendResolver.Resolve(_services, _config, parameters);
        if (!resolved.IsSuccess || resolved.Value is null)
            return ToolResult.Fail(string.Join("; ", resolved.Errors));

        if (!parameters.TryGetValue("module_directory", out var dirRaw)
            || dirRaw is not string moduleDirectory || string.IsNullOrWhiteSpace(moduleDirectory))
            return ToolResult.Fail("Required parameter 'module_directory' is missing or empty.");

        var result = await resolved.Value.ScanAsync(moduleDirectory.Trim(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return ToolResult.Fail(string.Join("; ", result.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(result.Value, SerializerOptions));
    }
}
