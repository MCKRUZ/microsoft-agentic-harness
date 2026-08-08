using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// A throwaway built-in skill on disk, plus everything needed to hang a probe tool off it and invoke
/// that tool through the REAL governed tool path on a <see cref="CompositionRootTestHost"/> provider.
/// </summary>
/// <remarks>
/// <para>
/// Composition-root tests of the tool-call chokepoint all need the same four things: a discoverable
/// skill (skills are read from disk, so a real directory is unavoidable), a tool built through the
/// production <see cref="IToolChainBuilder"/> so it arrives wrapped in the governance decorator, a
/// turn shaped exactly like <c>ExecuteAgentTurnCommandHandler</c>'s, and the invocation result read
/// back as text. Hand-rolling those per test class produced two near-identical copies immediately.
/// </para>
/// <para>
/// <strong>The turn setup is the part worth centralizing.</strong> It publishes both the scope's
/// governor and its observer chain ambiently, because that is what the production handler does — a
/// test that arms only some of them is testing a turn shape no host ever builds, and would go on
/// passing if a gate stopped being consulted. Every ambient scope is opened with <c>Begin</c> and
/// disposed, never assigned-then-nulled, since nulling on teardown disarms whatever an enclosing
/// flow armed.
/// </para>
/// </remarks>
internal sealed class GovernedToolTestSkill : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _label;

    /// <summary>
    /// Creates the skills directory and writes a single minimal skill into it.
    /// </summary>
    /// <param name="label">
    /// Short name for the test class using this, e.g. <c>"approval"</c>. Distinguishes the temp
    /// directory, the skill id, and the agent/conversation identity so a failure names its owner.
    /// </param>
    public GovernedToolTestSkill(string label)
    {
        _label = label;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"composition-{label}-" + Guid.NewGuid().ToString("N"));
        SkillsBasePath = Path.Combine(_tempRoot, "skills");
        SkillId = $"{label}-host-skill";

        var skillDir = Path.Combine(SkillsBasePath, "host");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"""
            ---
            name: {SkillId}
            description: A built-in skill used to resolve a governed tool.
            ---
            Host instructions.
            """);
    }

    /// <summary>The value to bind to <c>AppConfig:AI:Skills:BasePath</c>.</summary>
    public string SkillsBasePath { get; }

    /// <summary>The id of the skill written to <see cref="SkillsBasePath"/>.</summary>
    public string SkillId { get; }

    /// <summary>
    /// Resolves <paramref name="probe"/> back out of the production tool chain, wrapped in whatever
    /// governance decorator the composition applies.
    /// </summary>
    /// <param name="provider">A provider built by <see cref="CompositionRootTestHost"/>.</param>
    /// <param name="probe">The bare test function to smuggle in as an additional tool.</param>
    /// <returns>The governed wrapper around <paramref name="probe"/>.</returns>
    public async Task<AIFunction> BuildGovernedToolAsync(ServiceProvider provider, AIFunction probe)
    {
        var skill = provider.GetRequiredService<ISkillMetadataRegistry>().TryGet(SkillId);
        skill.Should().NotBeNull("the built-in skill must be discoverable from the configured BasePath");

        var tools = await provider.GetRequiredService<IToolChainBuilder>().BuildToolsAsync(
            skill!, new Domain.AI.Skills.SkillAgentOptions { AdditionalTools = [probe] });

        return tools.OfType<AIFunction>().Single(t => t.Name == probe.Name);
    }

    /// <summary>
    /// Invokes a governed function inside a turn shaped exactly like
    /// <c>ExecuteAgentTurnCommandHandler</c>'s — scoped context initialized, and the scope's governor
    /// and observer chain both published ambiently for the duration.
    /// </summary>
    /// <param name="scope">A scope from the composition-root provider.</param>
    /// <param name="function">The governed function returned by <see cref="BuildGovernedToolAsync"/>.</param>
    /// <returns>The raw invocation result and the governor's trace for the turn.</returns>
    public async Task<(object? Result, GovernanceTrace Trace)> InvokeUnderGovernedTurnAsync(
        IServiceScope scope, AIFunction function)
    {
        scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>()
            .Initialize($"composition-{_label}-agent", $"conv-{_label}", turnNumber: 1);

        // One value to arm, resolved from the production graph. This used to arm the governor and the
        // observer chain by hand and leave the classification gate and loop guard unarmed — so a
        // composition test could not have caught a turn that skipped either of them.
        var admissionPipeline = scope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();

        using var armed = ToolAdmissionAccessor.Begin(admissionPipeline);

        var result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
        return (result, admissionPipeline.GetTrace());
    }

    /// <summary>
    /// Reads an invocation result as the text the model would see. A governed function returns a
    /// refusal as a plain string but a successful call's value as a <c>JsonElement</c>, so asserting
    /// on <c>ToString()</c> alone silently compares against a quoted JSON literal.
    /// </summary>
    /// <param name="invocationResult">The value returned by <c>AIFunction.InvokeAsync</c>.</param>
    /// <returns>The unwrapped text.</returns>
    public static string ResultText(object? invocationResult) => invocationResult switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
            => element.GetString()!,
        _ => invocationResult?.ToString() ?? string.Empty,
    };

    /// <summary>Removes the temp skills directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
