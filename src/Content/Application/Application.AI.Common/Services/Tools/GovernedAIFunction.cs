using System.Text.Json;
using Application.AI.Common.Services.Governance;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Wraps an agent tool function so governance and progress checks run immediately before the tool executes.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="DelegatingAIFunction"/> so the wrapped function's name, description, and
/// JSON schema are preserved unchanged — only invocation is intercepted. On invoke it consults, in order:
/// the ambient <c>IToolInvocationGovernor</c> (via <see cref="ToolGovernanceAccessor"/>) — a denial
/// returns the governor's model-facing message in place of the tool result and the inner function is
/// never called; then the ambient <c>IProgressEvaluator</c> (via <see cref="ProgressGuardAccessor"/>) —
/// a spin verdict returns its halt message in place of the tool result. Progress is evaluated only for
/// calls the governor permits (a denied call never executed, so it must not count toward progress).
/// When neither is ambient (a tool invoked outside a governed turn), the call passes straight through.
/// </para>
/// <para>
/// This is the single invocation-time chokepoint for the agent's autonomous tool calls, applied to
/// every converted tool regardless of source (keyed-DI, MCP, or skill-provided).
/// </para>
/// </remarks>
internal sealed class GovernedAIFunction : DelegatingAIFunction
{
    // Unit-separator (U+001F) cannot appear in a JSON-serialised value, so distinct argument sets
    // cannot collide into the same joined signature. Built from a char code to keep the source ASCII.
    private static readonly string ArgPairSeparator = ((char)0x1F).ToString();

    public GovernedAIFunction(AIFunction innerFunction)
        : base(innerFunction)
    {
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var governor = ToolGovernanceAccessor.Current;
        if (governor is not null)
        {
            var decision = await governor.AuthorizeAsync(Name, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return decision.DeniedMessage ?? $"Error: tool '{Name}' was blocked by governance policy.";
        }

        // Progress / spin guard runs after authorization so it only sees calls that would actually
        // execute. Inert unless an evaluator is ambient and the guard is opt-in enabled.
        var progress = ProgressGuardAccessor.Current;
        if (progress is not null)
        {
            var verdict = progress.Evaluate(Name, () => ComputeArgumentsSignature(arguments));
            if (verdict.ShouldHalt)
                return verdict.HaltMessage
                    ?? $"Error: tool '{Name}' was stopped because the agent is not making progress.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a stable, deterministic signature of the call arguments so the progress evaluator can
    /// recognise identical calls. Keys are ordered; each value is JSON-serialised, falling back to its
    /// type name if serialisation throws — the signature is always computable and never throws on the
    /// agent's hot path.
    /// </summary>
    private static string? ComputeArgumentsSignature(AIFunctionArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return string.Empty;

        var parts = new List<string>(arguments.Count);
        foreach (var kvp in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            string value;
            try
            {
                value = kvp.Value is null ? "null" : JsonSerializer.Serialize(kvp.Value);
            }
            catch
            {
                value = kvp.Value?.GetType().FullName ?? "null";
            }

            parts.Add(string.Concat(kvp.Key, "=", value));
        }

        return string.Join(ArgPairSeparator, parts);
    }
}
