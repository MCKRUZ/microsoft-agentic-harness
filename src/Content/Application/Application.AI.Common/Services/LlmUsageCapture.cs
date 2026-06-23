using Application.AI.Common.Interfaces;
using Application.AI.Common.Pricing;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services;

/// <summary>
/// Scoped accumulator for LLM token usage within a single agent turn.
/// Records usage from multiple chat client calls (e.g. tool-use flows)
/// and computes cost using configured model pricing.
/// </summary>
public sealed class LlmUsageCapture : ILlmUsageCapture
{
    private static readonly AsyncLocal<ILlmUsageCapture?> s_current = new();

    /// <summary>
    /// Ambient capture instance for the current async flow.
    /// Set by the handler before agent execution so middleware in the
    /// singleton-scoped chat client pipeline can record to the correct
    /// scoped instance.
    /// </summary>
    public static ILlmUsageCapture? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }

    private readonly Dictionary<string, ModelPricingEntry> _pricing;
    private readonly string _defaultModel;
    private readonly object _lock = new();

    private int _inputTokens;
    private int _outputTokens;
    private int _cacheRead;
    private int _cacheWrite;
    private string? _model;
    private readonly HashSet<string> _toolNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-CallId invocation capture. Tool call requests land here keyed by their
    /// LLM-supplied call id; the matching result message updates the same entry
    /// on the next turn (when the FunctionResultContent is submitted back).
    /// CallId-less requests use a synthetic key so each one still lands as its
    /// own invocation.
    /// </summary>
    private readonly Dictionary<string, MutableInvocation> _invocations = new(StringComparer.Ordinal);

    private sealed class MutableInvocation
    {
        public required string? CallId { get; init; }
        public required string ToolName { get; set; }
        public string? ArgsJson { get; set; }
        public string? Stdout { get; set; }
    }

    public LlmUsageCapture(IOptionsMonitor<AppConfig> appConfig)
    {
        var config = appConfig.CurrentValue.Observability.LlmPricing;
        _pricing = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Models)
            _pricing[entry.Name] = entry;
        _defaultModel = config.DefaultModel;
    }

    public void Record(int inputTokens, int outputTokens, int cacheRead, int cacheWrite, string? model)
    {
        lock (_lock)
        {
            _inputTokens += inputTokens;
            _outputTokens += outputTokens;
            _cacheRead += cacheRead;
            _cacheWrite += cacheWrite;
            _model ??= model;
        }
    }

    public void RecordToolCall(string toolName)
    {
        lock (_lock)
        {
            _toolNames.Add(toolName);
        }
    }

    public void RecordToolRequest(string? callId, string toolName, string? argsJson)
    {
        if (string.IsNullOrEmpty(toolName))
            return;

        lock (_lock)
        {
            _toolNames.Add(toolName);

            var key = callId ?? $"__nocallid__{_invocations.Count}";
            if (_invocations.TryGetValue(key, out var existing))
            {
                existing.ToolName = toolName;
                existing.ArgsJson ??= argsJson;
            }
            else
            {
                _invocations[key] = new MutableInvocation
                {
                    CallId = callId,
                    ToolName = toolName,
                    ArgsJson = argsJson,
                };
            }
        }
    }

    public void RecordToolResult(string? callId, string? stdout)
    {
        if (callId is null)
            return; // can't pair without an id; leave as-is

        lock (_lock)
        {
            if (_invocations.TryGetValue(callId, out var existing))
            {
                existing.Stdout = stdout;
            }
            else
            {
                // Result arriving before request (shouldn't happen with current
                // middleware ordering, but keep the data rather than dropping it).
                _invocations[callId] = new MutableInvocation
                {
                    CallId = callId,
                    ToolName = string.Empty,
                    Stdout = stdout,
                };
            }
        }
    }

    public LlmUsageSnapshot TakeSnapshot()
    {
        lock (_lock)
        {
            // A null per-call model (the chat client frequently doesn't surface ModelId
            // per request — the model is configured on the client) must still price. Fall
            // back to the configured DefaultModel so cost is computed and the stored model
            // is meaningful instead of null — the cause of $0 cost on 216/217 prod messages.
            var model = _model ?? _defaultModel;
            var cost = ComputeCost(_inputTokens, _outputTokens, _cacheRead, _cacheWrite, model);
            var totalInput = _inputTokens + _cacheRead;
            var cacheHitPct = totalInput > 0 ? (decimal)_cacheRead / totalInput : 0m;

            var toolNames = _toolNames.Count > 0
                ? (IReadOnlyList<string>)_toolNames.ToList()
                : Array.Empty<string>();

            IReadOnlyList<ToolInvocationCapture> invocations = _invocations.Count > 0
                ? _invocations.Values
                    .Where(i => !string.IsNullOrEmpty(i.ToolName))
                    .Select(i => new ToolInvocationCapture(i.CallId, i.ToolName, i.ArgsJson, i.Stdout))
                    .ToList()
                : Array.Empty<ToolInvocationCapture>();

            var snapshot = new LlmUsageSnapshot(
                _inputTokens, _outputTokens, _cacheRead, _cacheWrite,
                model, cost, Math.Round(cacheHitPct, 4), toolNames)
            {
                ToolInvocations = invocations
            };

            _inputTokens = 0;
            _outputTokens = 0;
            _cacheRead = 0;
            _cacheWrite = 0;
            _model = null;
            _toolNames.Clear();
            _invocations.Clear();

            return snapshot;
        }
    }

    private decimal ComputeCost(int input, int output, int cacheRead, int cacheWrite, string? model)
    {
        if (model is null || !_pricing.TryGetValue(model, out var p))
            return 0m;

        return LlmCostCalculator.Compute(input, output, cacheRead, cacheWrite, p);
    }
}
