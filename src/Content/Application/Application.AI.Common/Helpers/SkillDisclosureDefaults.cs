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
    /// content is host-installed rather than user-supplied, and each tool is bounded by something other
    /// than the flag:
    /// </para>
    /// <list type="bullet">
    /// <item><c>load_skill</c> can only return a skill the harness registered for this agent — see
    /// <see cref="DisclosableSkillFactory"/>, which builds that list from the agent's assigned skills.</item>
    /// <item><c>read_skill_resource</c> can only reach a file registered as a resource on one of those
    /// skills. The name it takes is matched against that registered list, never resolved as a path, so
    /// there is no traversal surface to confine.</item>
    /// <item><c>run_skill_script</c> has nothing to run on the production path: no scripts are registered,
    /// so the lookup fails and the tool returns an error. Harness skill scripts execute through the
    /// sandboxed tool chain instead. <c>AgentEvaluationService</c>, which still loads skills from a
    /// directory, supplies a no-op runner for the same reason.</item>
    /// </list>
    /// <para>
    /// All three remain subject to the agent's allow-list through <c>ToolPermissionFilter</c>, which
    /// exempts only the two read-only tools. A consumer who registers scripts that really execute should
    /// put the authorisation decision where the script is visible, not on this flag.
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
