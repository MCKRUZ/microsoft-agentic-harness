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
    private const string FullLoad = "AppConfigHelper.LoadAppConfig(";

    [Fact]
    public void Main_Always_PreparesFoundryEnvironmentBeforeBuildingTheServiceProvider()
    {
        var path = RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        PreparesBeforeBuilding(code).Should().BeTrue(
            "the opt-out must remove Foundry's connection string before the composition root binds configuration");
    }

    /// <summary>
    /// The decision must read the FULL configuration. With a local-only loader (appsettings, User Secrets, environment),
    /// a deployer's opt-out kept in Key Vault is silently ignored and every other test stays green — the #904 round-1
    /// defect. Asserts the settings come from <c>AppConfigHelper.LoadAppConfig</c>, loaded before the preparation runs.
    /// </summary>
    [Fact]
    public void Main_Always_ReadsTheOptOutFromTheFullConfiguration()
    {
        var path = RepoRoot.Combine("src", "Content", "Presentation", "Presentation.FoundryHost", "Program.cs");
        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

        var fullLoad = code.IndexOf(FullLoad, StringComparison.Ordinal);
        var prepare = code.IndexOf(Prepare, StringComparison.Ordinal);
        fullLoad.Should().BeGreaterThanOrEqualTo(0, "the opt-out must be read from AppConfigHelper.LoadAppConfig, which includes Key Vault and App Configuration");
        fullLoad.Should().BeLessThan(prepare, "the full configuration must be loaded before it is handed to the preparation");
        code.Should().NotContain("LoadLocalAppConfig", "a local-only loader ignores an opt-out kept in Key Vault");
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
