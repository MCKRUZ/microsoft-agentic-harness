using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Tests.Common;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Pins #1071: the EvalRunner's shipped settings point at agent and skill folders that its build output contains.
/// Before, the host had no agents path and its skills path named a folder its output did not have, so every eval case
/// failed with "Skill '...' not found" whatever agent it named.
/// </summary>
/// <remarks>
/// The configured paths are resolved against this test's own output folder. It receives the EvalRunner's content items
/// through the project reference, the same items that reach the EvalRunner's output. The host resolves a relative
/// path against its base directory in the same way (<c>SkillContentRoots</c>). The default agent is the one checked
/// because it is the agent the October eval case named.
/// </remarks>
public sealed class EvalRunnerShipsItsAgentsTests
{
    [Fact]
    public void ShippedSettings_AgentsPath_HoldsTheDefaultAgent()
    {
        var paths = ShippedConfig().AI.Agents.AllPaths.Select(ResolveAgainstOutput).ToList();

        paths.Should().NotBeEmpty("an eval case names its agent, so the host needs somewhere to find agents");
        paths.Should().Contain(path => File.Exists(Path.Combine(path, "default", "AGENT.md")));
    }

    [Fact]
    public void ShippedSettings_SkillsPath_HoldsTheDefaultAgentsSkill()
    {
        var paths = ShippedConfig().AI.Skills.AllPaths.Select(ResolveAgainstOutput).ToList();

        paths.Should().Contain(path => File.Exists(Path.Combine(path, "default-agent", "SKILL.md")));
    }

    private static AppConfig ShippedConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoRoot.Combine("src", "Content", "Presentation", "Presentation.EvalRunner", "appsettings.json"))
            .Build();

        return configuration.GetSection("AppConfig").Get<AppConfig>()
            ?? throw new InvalidOperationException("The EvalRunner's appsettings.json has no AppConfig section.");
    }

    private static string ResolveAgainstOutput(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, AppContext.BaseDirectory);
}
