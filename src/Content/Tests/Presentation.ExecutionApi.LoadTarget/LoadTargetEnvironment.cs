using Domain.AI.Planner;
using Infrastructure.AI.Tools;
using Tests.AI.Fakes;
using Tests.Common;

namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// Sets the configuration the load target needs as process environment variables, before the host
/// is built.
/// </summary>
/// <remarks>
/// <para>
/// Environment variables rather than <c>WebApplicationFactory</c>'s in-memory configuration: the
/// host's composition root reads <c>AppConfig</c> through <c>AppConfigHelper.LoadAppConfig</c>, its own
/// configuration builder, and parts of it are read eagerly while services are registered. Both that
/// builder and <c>WebApplication.CreateBuilder</c> add environment variables last, so a value set here
/// wins over the copied <c>appsettings</c> files and over any developer user-secrets, everywhere it
/// is read.
/// </para>
/// <para>
/// Telemetry export is off unless asked for. A developer's user-secrets may point the OTLP exporter at
/// a shared collector, and a load run would flood it; the runner's <c>-WithTelemetry</c> switch turns it
/// back on for a deliberate, separately measured run.
/// </para>
/// </remarks>
public static class LoadTargetEnvironment
{
    /// <summary>
    /// The <c>modelDeploymentKey</c> the workflow-runs scenario's LlmCall step names, and the skill the
    /// load target provides under that name.
    /// </summary>
    /// <remarks>
    /// Workflow submission validates <c>modelDeploymentKey</c> as a deployment, but
    /// <c>LlmCallStepExecutor</c> passes it to <c>RunConversationCommand</c> as the agent name, which the
    /// agent factory resolves as a skill id. With no skill of that name the step fails before the model
    /// is called, so the host as shipped cannot complete a submitted LLM step. The load target supplies a
    /// skill under the deployment's name so the scenario measures the workflow machinery; the mismatch
    /// itself is a product defect, reported separately rather than worked around in the host.
    /// </remarks>
    public const string WorkflowAgent = "gpt-4o";

    /// <summary>Applies the load target's settings to this process's environment.</summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="issuer">The local issuer whose tenant and client ids the host must expect.</param>
    /// <returns>The isolated folder holding this run's on-disk state (databases, audit trails).</returns>
    public static string Apply(LoadTargetOptions options, LocalTokenIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(issuer);

        // Fresh databases and audit trails per run, removed on exit, so one run's state never
        // inflates the next run's numbers.
        var stateRoot = TestStateRoot.Redirect("execapi-load-");
        var workflowSkills = WriteWorkflowSkill(stateRoot);

        foreach (var (key, value) in Settings(options, issuer, workflowSkills))
            Environment.SetEnvironmentVariable(key, value);

        return stateRoot;
    }

    private static string WriteWorkflowSkill(string stateRoot)
    {
        var root = Path.Combine(stateRoot, "workflow-skills");
        var skillDir = Directory.CreateDirectory(Path.Combine(root, WorkflowAgent));
        File.WriteAllText(Path.Combine(skillDir.FullName, "SKILL.md"),
            $"---\nname: \"{WorkflowAgent}\"\ndescription: \"Answers a workflow step.\"\nversion: \"1.0.0\"\n---\n\n" +
            "Answer the question directly.");
        return root;
    }

    /// <summary>The settings <see cref="Apply"/> writes, as environment-variable names and values.</summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="issuer">The local issuer whose tenant and client ids the host must expect.</param>
    /// <param name="workflowSkills">The folder holding the skill the workflow step resolves.</param>
    internal static IEnumerable<(string Key, string Value)> Settings(
        LoadTargetOptions options, LocalTokenIssuer issuer, string workflowSkills)
    {
        yield return ("AppConfig__AI__BundleExecution__Enabled", "true");
        // poll-storm polls live-stream runs whose stream it never opens, so they do no agent work; the
        // default 5-minute reservation window would expire them mid-storm and turn its polls into 404s.
        yield return ("AppConfig__AI__BundleExecution__StreamReservationTtl", "00:30:00");
        yield return ("AppConfig__AI__BundleExecution__Auth__AllowAnonymous", "false");
        yield return ("AppConfig__AI__BundleExecution__Auth__TenantId", issuer.TenantId);
        yield return ("AppConfig__AI__BundleExecution__Auth__ClientId", issuer.ClientId);
        yield return ("AppConfig__AI__WorkflowSubmission__Enabled", "true");

        // A chat-client route, so every model call goes through IChatClientFactory and therefore the
        // simulated model. A developer's config may select FoundryResponses, which builds agents
        // through IFoundryAgentProvider instead and would bypass the simulation entirely.
        yield return ("AppConfig__AI__AgentFramework__ClientType", "AzureOpenAI");
        yield return ("AppConfig__Observability__Exporters__Otlp__Enabled", options.WithTelemetry ? "true" : "false");
        yield return ("AppConfig__AI__DirectToolInvocation__Enabled", options.EnableToolInvocation ? "true" : "false");

        // A workflow's LlmCall step is admitted against the caller's envelope as the reserved plan
        // capability llm_call; without it every workflow-runs step is refused before the model is called.
        yield return ("AppConfig__AI__BundleExecution__Envelopes__Default__AllowedTools__0", PlanCapabilities.LlmCall);
        // Granting llm_call is not enough on its own: below the Autonomous ceiling a granted capability
        // still needs approval, and with no approver configured every step ends refused.
        yield return ("AppConfig__AI__BundleExecution__Envelopes__Default__AutonomyCeiling", "Autonomous");
        yield return ("AppConfig__AI__Skills__AdditionalPaths__0", workflowSkills);

        if (!options.EnableToolInvocation)
            yield break;

        // The demo tool, granted to every caller. The README is explicit that echo_* tools are fixtures
        // for smoke tests and must never be granted where it matters; a throwaway load target is exactly
        // where they belong.
        yield return ("AppConfig__AI__BundleExecution__Envelopes__Default__AllowedTools__1", EchoCalculateTool.ToolName);
    }

    /// <summary>Describes the model profile in one line, for the start-up banner.</summary>
    /// <param name="profile">The profile to describe.</param>
    public static string Describe(SimulatedModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"{profile.Name}: first token after {profile.TimeToFirstToken.TotalMilliseconds:0} ms, " +
               $"{(profile.TokensPerSecond == 0 ? "unthrottled" : $"{profile.TokensPerSecond:0} tokens/s")}, " +
               $"{profile.ResponseTokens} tokens, {profile.TransientFailureRate:P0} throttled";
    }
}
