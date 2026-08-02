using Microsoft.Agents.AI;

namespace Application.AI.Common.Helpers;

/// <summary>
/// The single source of truth for how this harness configures the framework's
/// <see cref="AgentSkillsProvider"/>. Every <see cref="AgentSkillsProviderBuilder"/> in the codebase must
/// apply <see cref="Configure"/>, so the production agent path and the evaluation path cannot drift into
/// disagreeing about whether skills are disclosable.
/// </summary>
public static class SkillDisclosureDefaults
{
    /// <summary>
    /// Turns off the framework's per-tool approval prompts on the three skill tools.
    /// </summary>
    /// <param name="options">The provider options being built.</param>
    /// <remarks>
    /// <para>
    /// The framework wraps all three skill tools in <c>ApprovalRequiredAIFunction</c> by default. That
    /// makes a call return a <c>ToolApprovalRequestContent</c> instead of invoking the tool, and expects
    /// the caller to send an approval message back on the next turn. <b>No turn-driver in this harness
    /// answers that</b> — there is no approval channel on any execution path — so an approval-wrapped tool
    /// does not prompt anyone, it simply never completes. Leaving the defaults in place makes
    /// <c>load_skill</c> permanently unusable, and progressive disclosure depends on it.
    /// </para>
    /// <para>
    /// This trades away no real control, because the approval flag was never the boundary here. Skill
    /// content is host-installed rather than user-supplied; <c>read_skill_resource</c> is confined to the
    /// wired skill directories; and <c>run_skill_script</c> reaches the harness's no-op script runner and
    /// executes nothing, while remaining subject to the agent's allow-list through
    /// <c>ToolPermissionFilter</c> — which exempts only the two read-only tools. A consumer who replaces
    /// the no-op runner with one that really executes should put the authorisation decision in that
    /// runner, where it can see the script, rather than relying on this flag.
    /// </para>
    /// </remarks>
    public static void Configure(AgentSkillsProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.DisableLoadSkillApproval = true;
        options.DisableReadSkillResourceApproval = true;
        options.DisableRunSkillScriptApproval = true;
    }
}
