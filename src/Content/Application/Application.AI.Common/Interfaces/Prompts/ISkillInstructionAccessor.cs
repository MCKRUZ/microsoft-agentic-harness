namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Per-request holder that carries the current agent's merged skill-instruction text from the
/// singleton <c>AgentExecutionContextFactory</c> into the scoped <c>SkillInstructions</c> prompt
/// section provider.
/// </summary>
/// <remarks>
/// <para>
/// The factory is a singleton but the prompt composer and its section providers are scoped. To
/// source the current agent's skill instructions into the <c>SkillInstructions</c> section, the
/// factory resolves this accessor from the live request scope (via <c>IAmbientRequestScope</c>),
/// stamps the merged instruction onto it, then invokes the composer — which reads the same scoped
/// instance back through the provider. This mirrors how <c>SessionStateSectionProvider</c> sources
/// its content from the scoped <c>IAgentExecutionContext</c>.
/// </para>
/// <para>
/// Registered scoped in DI. Outside a request scope, or before the factory stamps a value,
/// <see cref="Instructions"/> is <see langword="null"/> and the section yields no content.
/// </para>
/// </remarks>
public interface ISkillInstructionAccessor
{
    /// <summary>
    /// Gets the merged skill-instruction text for the current request, or <see langword="null"/>
    /// when nothing has been set for this scope.
    /// </summary>
    string? Instructions { get; }

    /// <summary>
    /// Sets the merged skill-instruction text for the current request scope.
    /// </summary>
    /// <param name="instructions">The merged instruction text; may be null or empty.</param>
    void Set(string? instructions);
}
