namespace Domain.Common.Config.AI.Governance;

/// <summary>
/// Configuration for the deterministic spin / no-progress guard on the agent's live tool-call path.
/// Bound from <c>AppConfig:AI:Governance:ProgressGuard</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// The guard watches the sequence of tool calls an agent makes within a turn and breaks the loop when
/// it detects the agent is spinning — either re-issuing the identical call repeatedly, or making a run
/// of calls that introduce no information it has not already seen. Detection is purely deterministic
/// (call-signature counting, no model involvement), so it is cheap and predictable.
/// </para>
/// <para>
/// <strong>Opt-in.</strong> Off unless <see cref="Enabled"/> is true, so default deployments are
/// unchanged. Independent of <c>GovernanceConfig.EnforceToolInvocation</c>: the progress guard can run
/// with tool-permission enforcement off and vice-versa — they answer different questions ("may this
/// tool run?" versus "is the agent making progress?").
/// </para>
/// </remarks>
public sealed class ProgressGuardConfig
{
    /// <summary>Whether the spin / no-progress guard is active on the agent tool path.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// How many times the <em>identical</em> call (same tool, same arguments) may fire consecutively
    /// before the guard breaks the loop. A value below 2 disables this detector (a single call can
    /// never trip it). Default 3.
    /// </summary>
    public int RepetitionThreshold { get; init; } = 3;

    /// <summary>
    /// How many consecutive tool calls may introduce no new call signature before the guard breaks the
    /// loop. This catches multi-tool cycles (A→B→A→B…) that the consecutive-repetition detector misses:
    /// once this many calls in a row have all repeated previously-seen signatures, the agent is making
    /// no progress. A value below 2 disables this detector. Default 6.
    /// </summary>
    public int NoProgressWindow { get; init; } = 6;

    /// <summary>
    /// What the guard does when a spin is detected. <see cref="ProgressGuardAction.Stop"/> (the default)
    /// breaks the loop locally with a model-facing message; <see cref="ProgressGuardAction.Escalate"/>
    /// additionally raises an escalation reason code on the governance trace.
    /// </summary>
    public ProgressGuardAction OnSpin { get; init; } = ProgressGuardAction.Stop;
}

/// <summary>
/// The action the <see cref="ProgressGuardConfig"/> takes when a spin is detected.
/// </summary>
public enum ProgressGuardAction
{
    /// <summary>
    /// Break the loop locally: the spinning tool call is not executed and the model receives a halt
    /// message asking it to change approach or summarize. No human escalation is raised.
    /// </summary>
    Stop,

    /// <summary>
    /// Break the loop <em>and</em> raise an escalation reason code on the governance trace so a
    /// supervisor / human-in-the-loop signal is recorded for the turn.
    /// </summary>
    Escalate
}
