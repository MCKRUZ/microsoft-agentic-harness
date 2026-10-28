using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Asserts how <c>Program.Main</c> wires the deployer's telemetry opt-out (#904).
/// <see cref="FoundryHostBootstrap.PrepareFoundryEnvironment"/> is unit-tested on its own; these guard the two facts
/// about its call site that make it matter, and that no unit test of the method can see:
/// <list type="number">
///   <item>It runs before <c>BuildValidatedServiceProvider</c>. Called afterwards, the configuration would already be
///   bound with Foundry's connection string, and the hosting layer would export to Foundry's resource.</item>
///   <item>It is handed the FULL configuration (<c>AppConfigHelper.LoadAppConfig</c>). Handed a local-only one, an
///   opt-out kept in Key Vault would be silently ignored with every other test still green.</item>
/// </list>
/// </summary>
public sealed class ProgramTelemetryOptOutWiringTests
{
    // The full configuration is loaded into one local, that local is what the preparation receives, and nothing
    // reassigns it in between. Matched on comment- and string-stripped source.
    private static readonly Regex FullLoad = new(@"\bvar\s+settings\s*=\s*AppConfigHelper\.LoadAppConfig\(", RegexOptions.CultureInvariant);
    private static readonly Regex PrepareWithSettings = new(@"FoundryHostBootstrap\.PrepareFoundryEnvironment\(\s*settings\s*,", RegexOptions.CultureInvariant);
    private static readonly Regex AnyPrepare = new(@"FoundryHostBootstrap\.PrepareFoundryEnvironment\(", RegexOptions.CultureInvariant);
    private static readonly Regex SettingsAssignment = new(@"\bsettings\s*=(?!=)", RegexOptions.CultureInvariant);
    private const string Build = ".BuildValidatedServiceProvider(";

    [Fact]
    public void Main_Always_PreparesFoundryEnvironmentBeforeBuildingTheServiceProvider()
    {
        var code = ProgramSource();

        var prepare = AnyPrepare.Match(code);
        prepare.Success.Should().BeTrue("Program.Main must call FoundryHostBootstrap.PrepareFoundryEnvironment");
        var build = code.IndexOf(Build, StringComparison.Ordinal);
        build.Should().BeGreaterThanOrEqualTo(0, "Program.Main must build the service provider");
        prepare.Index.Should().BeLessThan(build,
            "the opt-out must remove Foundry's connection string before the composition root binds configuration");
    }

    [Fact]
    public void Main_Always_HandsThePreparationTheFullConfiguration()
    {
        var code = ProgramSource();

        WiringProblem(code).Should().BeNull();
    }

    [Theory]
    [InlineData("var settings = AppConfigHelper.LoadAppConfig(a);\nvar f = FoundryHostBootstrap.PrepareFoundryEnvironment(settings, r, w);", null)]
    [InlineData("var settings = AppConfigHelper.LoadAppConfig(a);\nvar f = FoundryHostBootstrap.PrepareFoundryEnvironment(new ConfigurationBuilder().AddEnvironmentVariables().Build(), r, w);", "argument")]
    [InlineData("var settings = LoadBootstrapConfig();\nvar f = FoundryHostBootstrap.PrepareFoundryEnvironment(settings, r, w);", "full")]
    [InlineData("var settings = AppConfigHelper.LoadAppConfig(a);\nsettings = LoadBootstrapConfig();\nvar f = FoundryHostBootstrap.PrepareFoundryEnvironment(settings, r, w);", "reassigned")]
    [InlineData("var settings = AppConfigHelper.LoadAppConfig(a);", "missing")]
    public void WiringProblem_SampleSources_FlagsEachWayTheFullConfigurationCanBeLost(string source, string? expected)
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

    // Null when the preparation receives the full configuration; otherwise what is wrong, in words.
    private static string? WiringProblem(string code)
    {
        if (!AnyPrepare.IsMatch(code))
            return "the PrepareFoundryEnvironment call is missing";
        if (!FullLoad.IsMatch(code))
            return "settings is not loaded with AppConfigHelper.LoadAppConfig, so it is not the full configuration";
        if (!PrepareWithSettings.IsMatch(code))
            return "PrepareFoundryEnvironment is not given the loaded settings as its argument";
        if (SettingsAssignment.Matches(code).Count != 1)
            return "settings is reassigned after the full load";
        return FullLoad.Match(code).Index < PrepareWithSettings.Match(code).Index
            ? null
            : "the full configuration is loaded after the preparation runs";
    }
}
