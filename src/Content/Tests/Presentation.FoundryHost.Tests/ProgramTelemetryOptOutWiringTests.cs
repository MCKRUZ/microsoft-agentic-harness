using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Asserts that <c>Program.Main</c> applies the deployer's telemetry opt-out (#904) before it builds the composition
/// root. <see cref="FoundryHostBootstrap.PrepareFoundryEnvironment"/> is unit-tested on its own; this guards the one
/// line that makes it matter. Called after <c>BuildValidatedServiceProvider</c>, the opt-out would still compile and
/// still log, but the configuration would already be bound with Foundry's connection string, and the hosting layer
/// would export to Foundry's resource.
/// </summary>
public sealed class ProgramTelemetryOptOutWiringTests
{
    private const string Prepare = "FoundryHostBootstrap.PrepareFoundryEnvironment(";
    private const string Build = ".BuildValidatedServiceProvider(";

    [Fact]
    public void Main_Always_PreparesFoundryEnvironmentBeforeBuildingTheServiceProvider()
    {
        var path = RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        PreparesBeforeBuilding(code).Should().BeTrue(
            "the opt-out must remove Foundry's connection string before the composition root binds configuration");
    }

    [Fact]
    public void PreparesBeforeBuilding_SampleSources_DetectsOnlyACallBeforeTheBuild()
    {
        const string inOrder = "var f = FoundryHostBootstrap.PrepareFoundryEnvironment(s, r, w);\n" +
            "var p = services.BuildValidatedServiceProvider();";
        const string reversed = "var p = services.BuildValidatedServiceProvider();\n" +
            "var f = FoundryHostBootstrap.PrepareFoundryEnvironment(s, r, w);";
        const string missing = "var p = services.BuildValidatedServiceProvider();";
        const string commentOnly = "// FoundryHostBootstrap.PrepareFoundryEnvironment(s, r, w);\n" +
            "var p = services.BuildValidatedServiceProvider();";

        PreparesBeforeBuilding(SourceScan.StripCommentsAndStrings(inOrder)).Should().BeTrue();
        PreparesBeforeBuilding(SourceScan.StripCommentsAndStrings(reversed)).Should().BeFalse();
        PreparesBeforeBuilding(SourceScan.StripCommentsAndStrings(missing)).Should().BeFalse();
        PreparesBeforeBuilding(SourceScan.StripCommentsAndStrings(commentOnly)).Should().BeFalse();
    }

    private static bool PreparesBeforeBuilding(string code)
    {
        var prepare = code.IndexOf(Prepare, StringComparison.Ordinal);
        var build = code.IndexOf(Build, StringComparison.Ordinal);
        return prepare >= 0 && build >= 0 && prepare < build;
    }
}
