using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Factories;

// Static-prompt half of AgentExecutionContextFactory: composes the authoritative system prompt
// through ISystemPromptComposer when prompt composition is enabled. Fail-open by design — see the
// member's own doc for the four ways it falls back.
//
// Deliberately a plain comment, not an XML doc — see AgentExecutionContextFactory.ContextProviders.cs.
public partial class AgentExecutionContextFactory
{
    /// <summary>
    /// Builds the authoritative static system prompt via the scoped <see cref="ISystemPromptComposer"/>
    /// when <c>PromptComposition</c> is enabled. Fails open to <paramref name="legacyInstruction"/>
    /// (never throws): if no request scope is active, the composer/accessor cannot be resolved, or
    /// composition faults or yields empty, the legacy merged instruction is returned unchanged.
    /// </summary>
    /// <remarks>
    /// The factory is a singleton while the composer and its section providers are scoped, so the
    /// scoped services are resolved per invocation from the current request scope via
    /// <see cref="IAmbientRequestScope"/> — the same idiom used for the Knowledge/Learnings context
    /// providers. Only the authoritative static section types
    /// (<see cref="AuthoritativePromptSections.Default"/>) are composed; per-turn dynamic sections are
    /// deliberately excluded and remain on the <c>AIContextProvider</c> rail.
    /// </remarks>
    private async Task<string> ComposeStaticSystemPromptAsync(string agentName, string legacyInstruction)
    {
        var scope = _serviceProvider.GetService<IAmbientRequestScope>()?.Current;
        if (scope is null)
        {
            _logger.LogDebug(
                "PromptComposition enabled but no ambient request scope is active; using legacy instruction for {AgentName}",
                agentName);
            return legacyInstruction;
        }

        var composer = scope.GetService<ISystemPromptComposer>();
        var accessor = scope.GetService<ISkillInstructionAccessor>();
        if (composer is null || accessor is null)
        {
            _logger.LogDebug(
                "PromptComposition enabled but composer/accessor unavailable in the request scope; using legacy instruction for {AgentName}",
                agentName);
            return legacyInstruction;
        }

        try
        {
            // Source the current agent's merged skill instructions into the scoped section provider.
            accessor.Set(legacyInstruction);

            var budget = _appConfig.CurrentValue.AI?.ContextManagement?.PromptComposition?.TokenBudget ?? 8000;
            var composed = await composer.ComposeAsync(agentName, budget, AuthoritativePromptSections.Default);

            if (string.IsNullOrEmpty(composed))
            {
                _logger.LogDebug(
                    "PromptComposition produced an empty prompt for {AgentName}; using legacy instruction",
                    agentName);
                return legacyInstruction;
            }

            _logger.LogDebug("Composed authoritative static system prompt for agent {AgentName}", agentName);
            return composed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PromptComposition failed for agent {AgentName}; falling back to legacy instruction",
                agentName);
            return legacyInstruction;
        }
    }
}
