using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Tests.Fakes;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Wiring integration tests (audit item I2) for the skill-prerequisite conversation scope
/// (PR #121): through the REAL composition root, a skill declaring <c>prerequisites</c> must
/// build successfully because <c>AgentConversationCache</c> flows the conversation id into
/// <c>SkillAgentOptions.AdditionalProperties["conversationId"]</c> before the agent is built.
/// </summary>
/// <remarks>
/// <para>
/// The pre-fix bug: <c>AgentFactory.ResolvePrerequisiteScope</c> requires the conversation id
/// in the execution context's additional properties and throws when absent. Only unit tests
/// ever supplied that key — the live path (<c>IAgentConversationCache.GetOrCreateAsync</c>)
/// never did, so every prerequisite-bearing skill crashed every conversation turn while all
/// unit tests stayed green. This is the canonical "inert machinery" shape: production never
/// supplies the value the tests inject.
/// </para>
/// <para>
/// These tests resolve <see cref="IAgentConversationCache"/> from the graph
/// <c>GetServices()</c> builds — real <c>AgentFactory</c>, real
/// <c>AgentExecutionContextFactory</c>, real <c>SkillMetadataRegistry</c> discovering real
/// SKILL.md files from disk. Only <see cref="IChatClientFactory"/> (the external LLM boundary)
/// is replaced.
/// </para>
/// </remarks>
public sealed class SkillPrerequisiteCompositionTests : IDisposable
{
    private const string ValidateSkillId = "validate-skill";
    private const string DeploySkillId = "deploy-skill";

    private readonly string _skillsDir;

    public SkillPrerequisiteCompositionTests()
    {
        _skillsDir = Path.Combine(Path.GetTempPath(), "composition-prereq-" + Guid.NewGuid().ToString("N"));

        WriteSkill("validate", $"""
            ---
            name: {ValidateSkillId}
            description: Validates the change before deployment.
            ---
            Validate.
            """);
        WriteSkill("deploy", $"""
            ---
            name: {DeploySkillId}
            description: Deploys the change after validation.
            prerequisites: [{ValidateSkillId}]
            ---
            Deploy.
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_skillsDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private void WriteSkill(string folder, string skillMarkdown)
    {
        var dir = Path.Combine(_skillsDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMarkdown);
    }

    private ServiceProvider BuildProvider() => CompositionRootTestHost.BuildProvider(
        new Dictionary<string, string?>
        {
            ["AppConfig:AI:Skills:BasePath"] = _skillsDir,
            ["AppConfig:AI:AgentFramework:ClientType"] = "AzureOpenAI",
            ["AppConfig:AI:AgentFramework:DefaultDeployment"] = "gpt-4o",
        },
        overrideServices: (services, _) =>
            // Replace ONLY the external LLM boundary. Last-registered wins, so the fake
            // shadows the production ChatClientFactory registration.
            services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory()));

    [Fact]
    public async Task SkillRegistry_DeploySkill_DiscoversPrerequisitesFromDisk()
    {
        await using var provider = BuildProvider();

        var skill = provider.GetRequiredService<ISkillMetadataRegistry>().TryGet(DeploySkillId);

        skill.Should().NotBeNull("the skill must be discoverable from the configured BasePath");
        skill!.Prerequisites.Should().ContainSingle()
            .Which.Should().Be(ValidateSkillId,
                "prerequisite metadata must survive the real parse+discovery path or the middleware never arms");
    }

    [Fact]
    public async Task GetOrCreateAsync_PrerequisiteSkill_ResolvesConversationScopeThroughCompositionRoot()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<IAgentConversationCache>();

        // Production shape: the caller supplies NO conversationId in the options — the cache
        // is the only component holding it and must flow it into the agent build.
        var act = () => cache.GetOrCreateAsync(
            "conv-composition-root", [ValidateSkillId, DeploySkillId], new SkillAgentOptions());

        // Pre-#121 this threw InvalidOperationException from AgentFactory.ResolvePrerequisiteScope
        // ("no conversation scope") on every turn of every prerequisite-bearing skill.
        var agent = await act.Should().NotThrowAsync(
            "the live cache→factory path must supply the prerequisite conversation scope itself");
        agent.Subject.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_SameConversation_ReturnsCachedAgentInstance()
    {
        // Companion wiring guarantee: the conversation scope injected by the cache must not
        // break agent identity caching — a second turn on the same conversation reuses the
        // same agent instead of rebuilding (and re-scoping) it.
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<IAgentConversationCache>();

        var first = await cache.GetOrCreateAsync(
            "conv-reuse", [ValidateSkillId, DeploySkillId], new SkillAgentOptions());
        var second = await cache.GetOrCreateAsync(
            "conv-reuse", [ValidateSkillId, DeploySkillId], new SkillAgentOptions());

        second.Should().BeSameAs(first);
    }
}
