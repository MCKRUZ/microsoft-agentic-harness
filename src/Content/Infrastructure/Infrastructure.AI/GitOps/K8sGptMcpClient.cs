using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.GitOps;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.GitOps;

/// <summary>
/// MCP-backed implementation of <see cref="IK8sGptMcpClient"/>. Resolves the
/// K8sGPT MCP server tools via <see cref="IMcpToolProvider"/>, invokes the
/// <c>analyze</c> tool, and deserializes the response into the harness's
/// neutral <see cref="K8sGptAnalysisResult"/> shape.
/// </summary>
/// <remarks>
/// <para>
/// The K8sGPT MCP server name comes from <c>AppConfig.AI.GitOps.K8sGptMcpServerName</c>
/// (default <c>"k8sgpt"</c>). The startup validator refuses to boot when that
/// server is not configured under <c>AppConfig.AI.McpServers.Servers</c>.
/// </para>
/// <para>
/// This client is intentionally read-only. K8sGPT's tool surface may evolve
/// to include mutating operations; this client narrows it to <c>analyze</c>
/// by contract and refuses to expose any other operation.
/// </para>
/// </remarks>
public sealed class K8sGptMcpClient : IK8sGptMcpClient
{
    private const string K8sGptAnalyzeToolName = "analyze";

    private readonly IMcpToolProvider _mcpToolProvider;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<K8sGptMcpClient> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="K8sGptMcpClient"/>.</summary>
    public K8sGptMcpClient(
        IMcpToolProvider mcpToolProvider,
        IOptionsMonitor<AppConfig> config,
        ILogger<K8sGptMcpClient> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(mcpToolProvider);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _mcpToolProvider = mcpToolProvider;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<Result<K8sGptAnalysisResult>> AnalyzeAsync(
        K8sGptAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var serverName = _config.CurrentValue.AI.GitOps.K8sGptMcpServerName;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return Result<K8sGptAnalysisResult>.Fail("gitops.k8sgpt.server_name_not_configured");
        }

        try
        {
            var tools = await _mcpToolProvider.GetToolsAsync(serverName, cancellationToken).ConfigureAwait(false);
            var analyzeTool = tools.OfType<AIFunction>().FirstOrDefault(
                f => string.Equals(f.Name, K8sGptAnalyzeToolName, StringComparison.OrdinalIgnoreCase));

            if (analyzeTool is null)
            {
                _logger.LogWarning("K8sGPT MCP server '{Server}' does not expose an 'analyze' tool.", serverName);
                return Result<K8sGptAnalysisResult>.Fail("gitops.k8sgpt.analyze_tool_missing");
            }

            var args = new Dictionary<string, object?>
            {
                ["namespace"] = request.Namespace,
                ["filters"] = request.Filters,
                ["explain"] = request.Explain
            };

            var rawResponse = await analyzeTool.InvokeAsync(
                new AIFunctionArguments(args),
                cancellationToken).ConfigureAwait(false);

            return Result<K8sGptAnalysisResult>.Success(ParseResponse(rawResponse));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "K8sGPT MCP analyze call failed.");
            return Result<K8sGptAnalysisResult>.Fail("gitops.k8sgpt.unexpected_error");
        }
    }

    private K8sGptAnalysisResult ParseResponse(object? raw)
    {
        var capturedAt = _timeProvider.GetUtcNow();
        if (raw is null)
        {
            return new K8sGptAnalysisResult { CapturedAt = capturedAt };
        }

        // K8sGPT analyze responses are JSON; the MCP layer typically returns
        // them as a string or JsonElement depending on transport.
        var json = raw switch
        {
            string s => s,
            JsonElement el => el.GetRawText(),
            _ => JsonSerializer.Serialize(raw)
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var findings = new List<K8sGptFinding>();
            if (root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsEl.EnumerateArray())
                {
                    findings.Add(ParseFinding(item));
                }
            }

            string? explanation = null;
            if (root.TryGetProperty("explanation", out var explEl) && explEl.ValueKind == JsonValueKind.String)
            {
                explanation = explEl.GetString();
            }

            return new K8sGptAnalysisResult
            {
                CapturedAt = capturedAt,
                Findings = findings,
                Explanation = explanation
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "K8sGPT response was not valid JSON; returning empty result.");
            return new K8sGptAnalysisResult { CapturedAt = capturedAt };
        }
    }

    private static K8sGptFinding ParseFinding(JsonElement el)
    {
        var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString() ?? string.Empty : string.Empty;
        var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty : string.Empty;
        var ns = el.TryGetProperty("namespace", out var nsEl) && nsEl.ValueKind == JsonValueKind.String
            ? nsEl.GetString() : null;
        var summary = el.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
            ? errEl.GetString() ?? string.Empty : string.Empty;
        var sev = el.TryGetProperty("severity", out var sevEl) && sevEl.ValueKind == JsonValueKind.String
            ? ParseSeverity(sevEl.GetString()) : K8sGptSeverity.Low;

        return new K8sGptFinding
        {
            Kind = kind,
            Name = name,
            Namespace = ns,
            Summary = summary,
            Severity = sev
        };
    }

    private static K8sGptSeverity ParseSeverity(string? raw) => raw?.ToLowerInvariant() switch
    {
        "high" or "critical" => K8sGptSeverity.High,
        "medium" or "warning" => K8sGptSeverity.Medium,
        _ => K8sGptSeverity.Low
    };
}
