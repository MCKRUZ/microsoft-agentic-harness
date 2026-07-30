using System.Text.Json;

namespace Domain.AI.Planner;

/// <summary>
/// The output a step parked in <see cref="StepExecutionStatus.Blocked"/> carries: a reference to the
/// escalation whose verdict decides its fate.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the shape is a contract between parties that never call each other. A human
/// gate writes it, the plan executor reads it back on resume to reconcile the block, and the run
/// substrate reads it to learn which decisions a parked run is waiting on. Three writers of the same
/// JSON literal is three chances for one of them to drift, and the failure it produces is silent: a
/// reader that finds no escalation reference concludes the step has none and leaves it blocked
/// forever.
/// </para>
/// <para>
/// It is deliberately not a general-purpose step-output envelope. A blocked step carries a reference
/// and nothing else — the escalation's own record holds the request, the approvers and the verdict —
/// and this stays that narrow so it cannot become the place step results accumulate.
/// </para>
/// </remarks>
public static class EscalationStepOutput
{
    /// <summary>JSON property under which the escalation identifier is stored.</summary>
    private const string EscalationIdProperty = "escalationId";

    /// <summary>Writes the output a step blocked on <paramref name="escalationId"/> carries.</summary>
    /// <param name="escalationId">The escalation whose verdict releases the step.</param>
    /// <returns>The JSON output to store against the blocked step.</returns>
    public static string Serialize(Guid escalationId) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string> { [EscalationIdProperty] = escalationId.ToString() });

    /// <summary>
    /// Reads the escalation identifier out of a blocked step's stored output.
    /// </summary>
    /// <param name="output">The step's stored output, which may be absent or not an escalation reference.</param>
    /// <returns>
    /// The referenced escalation, or <see langword="null"/> when the output is absent, is not JSON, or
    /// carries no escalation reference — all of which mean the same thing to every caller: there is no
    /// decision to look up.
    /// </returns>
    public static Guid? TryReadEscalationId(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(EscalationIdProperty, out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(idElement.GetString(), out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or corrupt. Indistinguishable from "no reference" to every caller, and a step
            // whose output cannot be read is left blocked rather than guessed at.
        }

        return null;
    }
}
