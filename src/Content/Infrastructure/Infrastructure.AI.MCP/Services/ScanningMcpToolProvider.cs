using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="IMcpToolProvider"/> so that every tool definition an external MCP server
/// advertises is security-scanned before it can reach the model, and withheld when a finding meets
/// the configured severity threshold.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this sits at discovery rather than on the call path.</strong> What an external MCP
/// server controls is the tool's name, description and parameter schema — text the harness copies
/// verbatim into the model's context so the model knows the tool exists. Tool poisoning, hidden
/// instructions, description injection and homoglyph typosquatting all act through that text, and
/// they act the moment it is in context, whether or not the tool is ever invoked. Withholding a
/// flagged tool at discovery is the only outcome that keeps the text out; refusing the call later
/// would be too late, and would also be redundant with the tool-call admission chain.
/// </para>
/// <para>
/// <strong>Why a decorator and not a change to <see cref="McpToolProvider"/>.</strong> Every
/// consumer of MCP tools resolves <see cref="IMcpToolProvider"/> from the container, so wrapping the
/// interface covers all of them at once and leaves the transport implementation focused on
/// connection handling. It is also the only shape that can be tested: the inner provider's
/// collaborator is a sealed connection manager, so a test cannot make it return a tool at all.
/// </para>
/// <para>
/// <strong>Every tool-returning method is screened, including
/// <see cref="GetToolByNameAsync"/>.</strong> The inner provider's own by-name lookup calls its own
/// <c>GetToolsAsync</c>, not this one, so delegating without re-screening would leave a hole that
/// resolves a withheld tool by asking for it directly.
/// </para>
/// <para>
/// <strong>Verdicts are deliberately not cached.</strong> Re-scanning on every discovery call looks
/// like waste — the work repeats per agent build — but the cost is a handful of regex matches over
/// short strings, and a cache keyed on the tool name would defeat the rug-pull threat this scanner
/// names explicitly: a server that advertises a clean description, earns its place in the tool
/// surface, and then changes that description would keep its cached verdict forever. Re-reading the
/// definition each time is the only thing that catches a definition that changed.
/// </para>
/// </remarks>
public sealed class ScanningMcpToolProvider : IMcpToolProvider
{
    /// <summary>
    /// Stands in for the server name on the by-name lookup path, where <see cref="IMcpToolProvider"/>
    /// does not report which server answered.
    /// </summary>
    private const string UnattributedServer = "unknown";

    private readonly IMcpToolProvider _inner;
    private readonly IMcpSecurityScanner _scanner;
    private readonly IOptionsMonitor<AIConfig> _config;
    private readonly ILogger<ScanningMcpToolProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanningMcpToolProvider"/> class.
    /// </summary>
    /// <param name="inner">The tool provider whose results are screened.</param>
    /// <param name="scanner">The scanner that inspects each tool definition.</param>
    /// <param name="config">Supplies the scanning policy; read per call so a config reload takes effect.</param>
    /// <param name="logger">Logger for withheld and flagged tools.</param>
    public ScanningMcpToolProvider(
        IMcpToolProvider inner,
        IMcpSecurityScanner scanner,
        IOptionsMonitor<AIConfig> config,
        ILogger<ScanningMcpToolProvider> logger)
    {
        _inner = inner;
        _scanner = scanner;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default) =>
        Screen(serverName, await _inner.GetToolsAsync(serverName, cancellationToken));

    /// <inheritdoc />
    public async Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await _inner.GetAllToolsAsync(cancellationToken);
        var screened = new Dictionary<string, IList<AITool>>(discovered.Count);

        foreach (var (serverName, tools) in discovered)
        {
            var admitted = Screen(serverName, tools);

            // A server whose every tool was withheld is dropped entirely, matching the inner
            // provider's contract of listing only servers that contributed tools.
            if (admitted.Count > 0)
                screened[serverName] = admitted;
        }

        return screened;
    }

    /// <inheritdoc />
    public async Task<AIFunction?> GetToolByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var tool = await _inner.GetToolByNameAsync(name, cancellationToken);
        return tool is not null && IsAdmitted(UnattributedServer, tool) ? tool : null;
    }

    /// <inheritdoc />
    public Task<bool> IsServerAvailableAsync(string serverName, CancellationToken cancellationToken = default) =>
        _inner.IsServerAvailableAsync(serverName, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Does not dispose the decorated provider — the DI container registers both as singletons and
    /// owns its lifetime, the same arrangement <see cref="McpToolProvider"/> has with
    /// <see cref="McpConnectionManager"/>.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>
    /// Returns the tools that survived the scan, preserving order.
    /// </summary>
    private IList<AITool> Screen(string serverName, IList<AITool> tools) =>
        [.. tools.Where(tool => IsAdmitted(serverName, tool))];

    /// <summary>
    /// Scans one tool definition and decides whether it may be published to the model, logging and
    /// counting anything the scanner flagged.
    /// </summary>
    private bool IsAdmitted(string serverName, AITool tool)
    {
        var policy = _config.CurrentValue.Governance;
        if (!policy.EnableMcpSecurity)
            return true;

        var result = _scanner.ScanTool(tool.Name, tool.Description, ExtractSchema(tool));

        // The threat-count check is not redundant with IsSafe. IMcpSecurityScanner is a public
        // contract a consumer can implement, and one returning IsSafe=false with no threats listed
        // would make the Max below throw on an empty sequence.
        if (result.IsSafe || result.Threats.Count == 0)
            return true;

        var highest = result.Threats.Max(threat => threat.Severity);
        var withheld = highest >= policy.McpToolBlockThreshold;

        // Findings are reported as threat-type/severity pairs only. The description that triggered
        // them is attacker-supplied text, and copying it into the log would move the injection
        // payload into whatever reads the logs next.
        var findings = string.Join(", ", result.Threats.Select(threat => $"{threat.ThreatType}/{threat.Severity}"));

        if (withheld)
        {
            GovernanceMetrics.McpToolsWithheld.Add(
                1, new TagList { { GovernanceConventions.McpThreatSeverityTag, highest.ToString() } });

            _logger.LogWarning(
                "Withholding MCP tool '{ToolName}' from server '{ServerName}': security scan found {Findings}. "
                + "Block threshold is {Threshold}.",
                tool.Name, serverName, findings, policy.McpToolBlockThreshold);
        }
        else
        {
            // Information, not Warning. Tool discovery re-runs on every agent build and on every
            // McpController request, so a server that permanently trips one of the lower-confidence
            // heuristics would otherwise emit a Warning per tool per turn forever — burying the
            // withheld-tool warnings, which are the ones that mean something happened.
            _logger.LogInformation(
                "MCP tool '{ToolName}' from server '{ServerName}' was flagged but published: {Findings} is below "
                + "the block threshold of {Threshold}.",
                tool.Name, serverName, findings, policy.McpToolBlockThreshold);
        }

        return !withheld;
    }

    /// <summary>
    /// Returns the tool's parameter schema flattened to its <em>decoded</em> property names and
    /// string values for scanning, or <see langword="null"/> when the tool exposes none. The schema
    /// is scanned as well as the description because it carries attacker-controlled property names
    /// and parameter descriptions into the same context window.
    /// </summary>
    /// <remarks>
    /// Decoding is the point, not a convenience. <c>JsonElement.ToString()</c> returns the raw JSON
    /// text with escape sequences intact, so a description containing a JSON-escaped
    /// <c>​</c> reaches the scanner as the six literal characters <c>​</c> and the
    /// invisible-character rule — the only Critical-severity rule — never matches. A hostile server
    /// escaping its hidden characters would have walked straight past the strictest check.
    /// </remarks>
    private static string? ExtractSchema(AITool tool)
    {
        if (tool is not AIFunction function || function.JsonSchema.ValueKind == JsonValueKind.Undefined)
            return null;

        var builder = new StringBuilder();
        AppendDecodedText(function.JsonSchema, builder);
        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Appends every property name and string value in the element, decoded, separated by spaces.
    /// </summary>
    private static void AppendDecodedText(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    builder.Append(property.Name).Append(' ');
                    AppendDecodedText(property.Value, builder);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AppendDecodedText(item, builder);

                break;

            case JsonValueKind.String:
                builder.Append(element.GetString()).Append(' ');
                break;
        }
    }
}
