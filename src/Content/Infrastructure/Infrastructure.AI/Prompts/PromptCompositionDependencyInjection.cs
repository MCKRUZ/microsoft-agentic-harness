using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Services.Prompts;
using Infrastructure.AI.Prompts.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// DI registrations for section-based system prompt composition.
/// </summary>
public static class PromptCompositionDependencyInjection
{
    /// <summary>
    /// Registers the system prompt composition pipeline: the singleton
    /// <see cref="IPromptSectionCache"/> (memoization survives across requests), the scoped
    /// <see cref="ISystemPromptComposer"/>, the built-in <see cref="IPromptSectionProvider"/>
    /// implementations, and the <see cref="IPromptCacheTracker"/>.
    /// </summary>
    /// <remarks>
    /// Section providers consume the scoped <c>IAgentExecutionContext</c>, so the composer is
    /// registered SCOPED — each request scope composes against its own live conversation state.
    /// A singleton composer would capture the root-scope context at first resolution and every
    /// conversation would see stale or cross-request session state (audit finding H2). Memoized
    /// cacheable sections still persist across requests because the backing
    /// <see cref="IPromptSectionCache"/> stays a singleton.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddSystemPromptComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPromptSectionCache, InMemoryPromptSectionCache>();
        services.AddScoped<ISystemPromptComposer, MemoizedPromptComposer>();
        // Scoped holder that carries the current request's merged skill instructions from the
        // singleton AgentExecutionContextFactory into the (scoped) SkillInstructions section.
        services.AddScoped<ISkillInstructionAccessor, SkillInstructionAccessor>();
        services.AddTransient<IPromptSectionProvider, AgentIdentitySectionProvider>();
        services.AddTransient<IPromptSectionProvider, SkillInstructionsSectionProvider>();
        // ToolSchemas and SessionState remain registered as available building blocks. They are
        // excluded from the AUTHORITATIVE static prompt (AuthoritativePromptSections.Default) — the
        // SDK already sends tool schemas via ChatOptions.Tools, and session state is per-turn dynamic
        // context served on the AIContextProvider rail — but stay available for direct/full composition.
        services.AddTransient<IPromptSectionProvider, ToolSchemasSectionProvider>();
        services.AddTransient<IPromptSectionProvider, PermissionRulesSectionProvider>();
        services.AddTransient<IPromptSectionProvider, SessionStateSectionProvider>();
        services.AddSingleton<IPromptCacheTracker, Sha256PromptCacheTracker>();

        return services;
    }
}
