using Application.AI.Common.Interfaces;
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
    private readonly object _lock = new();

    private int _inputTokens;
    private int _outputTokens;
    private int _cacheRead;
    private int _cacheWrite;
    private string? _model;
    private readonly HashSet<string> _toolNames = new(StringComparer.OrdinalIgnoreCase);

    public LlmUsageCapture(IOptionsMonitor<AppConfig> appConfig)
    {
        var config = appConfig.CurrentValue.Observability.LlmPricing;
        _pricing = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Models)
            _pricing[entry.Name] = entry;
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

    public LlmUsageSnapshot TakeSnapshot()
    {
        lock (_lock)
        {
            var cost = ComputeCost(_inputTokens, _outputTokens, _cacheRead, _cacheWrite, _model);
            var totalInput = _inputTokens + _cacheRead;
            var cacheHitPct = totalInput > 0 ? (decimal)_cacheRead / totalInput : 0m;

            var toolNames = _toolNames.Count > 0
                ? (IReadOnlyList<string>)_toolNames.ToList()
                : Array.Empty<string>();

            var snapshot = new LlmUsageSnapshot(
                _inputTokens, _outputTokens, _cacheRead, _cacheWrite,
                _model, cost, Math.Round(cacheHitPct, 4), toolNames);

            _inputTokens = 0;
            _outputTokens = 0;
            _cacheRead = 0;
            _cacheWrite = 0;
            _model = null;
            _toolNames.Clear();

            return snapshot;
        }
    }

    private decimal ComputeCost(int input, int output, int cacheRead, int cacheWrite, string? model)
    {
        if (model is null || !_pricing.TryGetValue(model, out var p))
            return 0m;

        return
            (input * p.InputPerMillion / 1_000_000m) +
            (output * p.OutputPerMillion / 1_000_000m) +
            (cacheRead * p.CacheReadPerMillion / 1_000_000m) +
            (cacheWrite * p.CacheWritePerMillion / 1_000_000m);
    }
}
