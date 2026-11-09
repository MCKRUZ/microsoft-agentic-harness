using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Asserts that the agent <c>Program.Main</c> hands Foundry's runtime is the screened one.
/// <see cref="FoundryInputScreeningTests"/> proves the wrapper screens; nothing there fails if the host stops serving the
/// wrapped agent, so these read the call sites that make the screening reach a caller:
/// <list type="number">
///   <item><c>BuildHostedAgentAsync</c> returns <c>FoundryInputScreening.Wrap(...)</c>.</item>
///   <item>That method's result is the one agent the host registers with <c>AddFoundryResponses</c>.</item>
/// </list>
/// </summary>
public sealed class ProgramInputScreeningWiringTests
{
    // The method's declaration, then its first return statement (a return inside a nested lambda would come first and fail).
    private static readonly Regex Declaration = new(@"Task<AIAgent>\s+BuildHostedAgentAsync\(", RegexOptions.CultureInvariant);
    private static readonly Regex FirstReturn = new(@"\breturn\b\s*(\S+?\()", RegexOptions.CultureInvariant);
    private static readonly Regex AgentFromBuild = new(@"\bagent\s*=\s*await\s+BuildHostedAgentAsync\(", RegexOptions.CultureInvariant);
    private static readonly Regex AgentAssignment = new(@"\bagent\s*=(?!=)", RegexOptions.CultureInvariant);
    private static readonly Regex ServesAgent = new(@"\.AddFoundryResponses\(\s*agent\s*,", RegexOptions.CultureInvariant);
    private static readonly Regex AnyServe = new(@"\.AddFoundryResponses\(", RegexOptions.CultureInvariant);

    [Fact]
    public void Main_Always_ServesTheScreenedAgent()
    {
        WiringProblem(ProgramSource()).Should().BeNull();
    }

    [Theory]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { var o = new O { A = 1 }; return FoundryInputScreening.Wrap(x, p); }\nagent = await BuildHostedAgentAsync(p, t);\nb.Services.AddFoundryResponses(agent, s);", null)]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { return await factory.Create(x); }\nagent = await BuildHostedAgentAsync(p, t);\nb.Services.AddFoundryResponses(agent, s);", "Wrap")]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { if (x) return Raw(x); return FoundryInputScreening.Wrap(x, p); }\nagent = await BuildHostedAgentAsync(p, t);\nb.Services.AddFoundryResponses(agent, s);", "Wrap")]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { return FoundryInputScreening.Wrap(x, p); }\nagent = Build(p);\nb.Services.AddFoundryResponses(agent, s);", "BuildHostedAgentAsync")]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { return FoundryInputScreening.Wrap(x, p); }\nagent = await BuildHostedAgentAsync(p, t);\nagent = other;\nb.Services.AddFoundryResponses(agent, s);", "reassigned")]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { return FoundryInputScreening.Wrap(x, p); }\nagent = await BuildHostedAgentAsync(p, t);\nb.Services.AddFoundryResponses(raw, s);", "registers")]
    [InlineData("static async Task<AIAgent> BuildHostedAgentAsync(IServiceProvider p, CancellationToken c) { return FoundryInputScreening.Wrap(x, p); }\nagent = await BuildHostedAgentAsync(p, t);\nb.Services.AddFoundryResponses(agent, s);\nb.Services.AddFoundryResponses(raw, s);", "registers")]
    public void WiringProblem_SampleSources_FlagsEachWayTheScreeningCanBeBypassed(string source, string? expected)
    {
        var problem = WiringProblem(SourceScan.StripCommentsAndStrings(source));

        if (expected is null)
            problem.Should().BeNull();
        else
            problem.Should().Contain(expected);
    }

    private static string ProgramSource()
    {
        var path = RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs");
        return SourceScan.StripCommentsAndStrings(File.ReadAllText(path));
    }

    // Null when the served agent is the screened one; otherwise what is wrong, in words.
    private static string? WiringProblem(string code)
    {
        var declaration = Declaration.Match(code);
        var firstReturn = declaration.Success ? FirstReturn.Match(code, declaration.Index + declaration.Length) : Match.Empty;
        if (!firstReturn.Success || firstReturn.Groups[1].Value != "FoundryInputScreening.Wrap(")
            return "BuildHostedAgentAsync does not return FoundryInputScreening.Wrap(...)";
        if (!AgentFromBuild.IsMatch(code))
            return "the served agent is not the result of BuildHostedAgentAsync";
        if (AgentAssignment.Matches(code).Count != 1)
            return "agent is reassigned after BuildHostedAgentAsync";
        if (AnyServe.Matches(code).Count != 1 || !ServesAgent.IsMatch(code))
            return "the host registers something other than the screened agent with AddFoundryResponses";
        return null;
    }
}
