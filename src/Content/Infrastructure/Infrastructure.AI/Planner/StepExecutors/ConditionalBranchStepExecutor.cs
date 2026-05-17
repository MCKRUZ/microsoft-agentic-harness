using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner.StepExecutors;

/// <summary>
/// Evaluates a condition expression against upstream outputs and activates the appropriate edge.
/// Pure logic — no external service dependencies.
/// </summary>
public sealed partial class ConditionalBranchStepExecutor : IPlanStepExecutor
{
    private static readonly Regex UnsafePattern = UnsafeExpressionRegex();
    private static readonly Regex AllowedPattern = AllowedExpressionRegex();

    private readonly ILogger<ConditionalBranchStepExecutor> _logger;

    public ConditionalBranchStepExecutor(ILogger<ConditionalBranchStepExecutor> logger)
    {
        _logger = logger;
    }

    public Task<StepExecutionResult> ExecuteAsync(
        PlanStep step,
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (step.Configuration is not ConditionalBranchConfig config)
        {
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"Step '{step.Name}' has invalid configuration type for ConditionalBranch executor."
            });
        }

        if (!IsExpressionSafe(config.ConditionExpression))
        {
            return Task.FromResult(new StepExecutionResult
            {
                Status = StepExecutionStatus.Failed,
                Duration = sw.Elapsed,
                ErrorMessage = $"Condition expression rejected: contains unsafe content."
            });
        }

        var context = BuildEvaluationContext(upstreamOutputs);
        var conditionMet = EvaluateCondition(config.ConditionExpression, context);

        sw.Stop();

        var target = conditionMet ? config.TrueEdgeTargetId : config.FalseEdgeTargetId;

        _logger.LogDebug("Conditional branch '{Step}': condition={Condition}, result={Result}, target={Target}",
            step.Name, config.ConditionExpression, conditionMet, target.Value);

        return Task.FromResult(new StepExecutionResult
        {
            Status = StepExecutionStatus.Completed,
            Output = conditionMet ? "true" : "false",
            Duration = sw.Elapsed,
            ActiveEdgeTarget = target
        });
    }

    private static bool IsExpressionSafe(string expression)
    {
        if (expression.Length > 500)
            return false;

        if (expression.Contains('.'))
            return false;

        if (UnsafePattern.IsMatch(expression))
            return false;

        return AllowedPattern.IsMatch(expression);
    }

    private static Dictionary<string, object?> BuildEvaluationContext(
        IReadOnlyDictionary<PlanStepId, string> upstreamOutputs)
    {
        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, output) in upstreamOutputs)
        {
            if (string.IsNullOrEmpty(output)) continue;

            try
            {
                using var doc = JsonDocument.Parse(output);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    context[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
            }
            catch (JsonException)
            {
                // Non-JSON output — skip
            }
        }

        return context;
    }

    private static bool EvaluateCondition(string expression, Dictionary<string, object?> context, int depth = 0)
    {
        if (depth > 10)
            return false;
        var normalized = expression
            .Replace(" AND ", " && ", StringComparison.OrdinalIgnoreCase)
            .Replace(" OR ", " || ", StringComparison.OrdinalIgnoreCase)
            .Replace("NOT ", "!", StringComparison.OrdinalIgnoreCase);

        // Split on && first (lower precedence), then ||
        if (normalized.Contains("&&"))
        {
            var parts = SplitRespectingParens(normalized, "&&");
            return parts.All(p => EvaluateCondition(p.Trim(), context, depth + 1));
        }

        if (normalized.Contains("||"))
        {
            var parts = SplitRespectingParens(normalized, "||");
            return parts.Any(p => EvaluateCondition(p.Trim(), context, depth + 1));
        }

        // Handle parentheses
        var trimmed = normalized.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
            return EvaluateCondition(trimmed[1..^1], context, depth + 1);

        // Handle negation
        if (trimmed.StartsWith('!'))
            return !EvaluateCondition(trimmed[1..].Trim(), context, depth + 1);

        // Comparison operators
        return EvaluateComparison(trimmed, context);
    }

    private static bool EvaluateComparison(string expression, Dictionary<string, object?> context)
    {
        string[] operators = [">=", "<=", "!=", "==", ">", "<"];

        foreach (var op in operators)
        {
            var idx = expression.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var left = ResolveValue(expression[..idx].Trim(), context);
            var right = ResolveValue(expression[(idx + op.Length)..].Trim(), context);

            return CompareValues(left, right, op);
        }

        // Boolean variable check
        var value = ResolveValue(expression, context);
        return value is true or (not null and not false and not 0.0);
    }

    private static object? ResolveValue(string token, Dictionary<string, object?> context)
    {
        if (token == "null") return null;
        if (token == "true") return true;
        if (token == "false") return false;
        if (double.TryParse(token, out var num)) return num;
        if (token.StartsWith('"') && token.EndsWith('"')) return token[1..^1];
        return context.TryGetValue(token, out var val) ? val : null;
    }

    private static bool CompareValues(object? left, object? right, string op)
    {
        if (left is double ld && right is double rd)
        {
            return op switch
            {
                ">=" => ld >= rd,
                "<=" => ld <= rd,
                ">" => ld > rd,
                "<" => ld < rd,
                "==" => Math.Abs(ld - rd) < 0.0001,
                "!=" => Math.Abs(ld - rd) >= 0.0001,
                _ => false
            };
        }

        var ls = left?.ToString();
        var rs = right?.ToString();

        return op switch
        {
            "==" => string.Equals(ls, rs, StringComparison.Ordinal),
            "!=" => !string.Equals(ls, rs, StringComparison.Ordinal),
            _ => false
        };
    }

    private static List<string> SplitRespectingParens(string expression, string delimiter)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = 0;

        for (var i = 0; i <= expression.Length - delimiter.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')') depth--;
            else if (depth == 0 && expression.AsSpan(i).StartsWith(delimiter))
            {
                parts.Add(expression[current..i]);
                current = i + delimiter.Length;
                i += delimiter.Length - 1;
            }
        }

        parts.Add(expression[current..]);
        return parts;
    }

    [GeneratedRegex(@"(System\.|File\.|Process\.|Reflection\.|Assembly\.|Type\.|Activator\.|Marshal\.|unsafe|dynamic|typeof|nameof)", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeExpressionRegex();

    [GeneratedRegex(@"^[\w\s\.\(\)>=<!&|""\d\-\+\*\/]+$")]
    private static partial Regex AllowedExpressionRegex();
}
