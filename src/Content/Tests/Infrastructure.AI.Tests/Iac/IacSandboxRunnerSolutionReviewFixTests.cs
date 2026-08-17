using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Regression tests for the solution-review finding that the IaC sandbox egress
/// allowlist was never enforced: <see cref="IacSandboxRunner"/> registered the
/// registry allowlist on the permission profile's now-removed host allowlist field
/// (#405) but never populated
/// <c>SandboxExecutionRequest.EgressPrecheckTargets</c>, so the only sandbox-side
/// egress gate (the preflight, which short-circuits on an empty target list) never
/// ran. These tests assert the runner now surfaces every registry host as a
/// preflight target so the active egress policy is consulted before the CLI spawns.
/// </summary>
public sealed class IacSandboxRunnerSolutionReviewFixTests
{
    private const string ModuleDir = "/tmp/iac/module";

    /// <summary>An empty-override resolver — these tests exercise egress precheck, not #405's merge.</summary>
    private static ToolPermissionProfileResolver NoOverrideResolver()
    {
        var services = new ServiceCollection();
        services.AddOptions<SandboxConfig>();
        var provider = services.BuildServiceProvider();
        var lookup = new FirstPartyToolLookup(provider, new HashSet<string>());
        return new ToolPermissionProfileResolver(lookup, provider.GetRequiredService<IOptionsMonitor<SandboxConfig>>());
    }

    [Fact]
    public async Task RunAsync_RegistryAllowlistConfigured_PopulatesEgressPrecheckTargetsForEveryHost()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var allowlist = new[] { "registry.terraform.io", "mcr.microsoft.com" };

        await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["init"],
            moduleDirectory: ModuleDir,
            registryAllowlist: allowlist,
            executor: sandbox,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            permissionResolver: NoOverrideResolver());

        var request = sandbox.RequestFor("terraform");

        // Old behavior: EgressPrecheckTargets was null, so the preflight short-circuited.
        request.EgressPrecheckTargets.Should().NotBeNullOrEmpty();
        request.EgressPrecheckTargets!.Select(u => u.Host)
            .Should().BeEquivalentTo(allowlist);
        request.EgressPrecheckTargets.Should().OnlyContain(u => u.Scheme == Uri.UriSchemeHttps);
    }

    [Fact]
    public async Task RunAsync_EmptyAllowlist_LeavesPreflightTargetsEmpty()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);

        await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["init"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            executor: sandbox,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            permissionResolver: NoOverrideResolver());

        // No declared registries means nothing to precheck — the preflight is allowed
        // to short-circuit only because there is genuinely no egress claim to enforce.
        sandbox.RequestFor("terraform").EgressPrecheckTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_DuplicateAndBlankHosts_DeduplicatesAndSkipsBlanks()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);

        await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["init"],
            moduleDirectory: ModuleDir,
            registryAllowlist: ["registry.terraform.io", "  ", "registry.terraform.io"],
            executor: sandbox,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            permissionResolver: NoOverrideResolver());

        var targets = sandbox.RequestFor("terraform").EgressPrecheckTargets!;
        targets.Select(u => u.Host).Should().ContainSingle().Which.Should().Be("registry.terraform.io");
    }

    [Fact]
    public async Task RunAsync_OperatorOverride_NonIntersectingDeny_ReachesTheRunner()
    {
        // Regression for #405's bypass gap: IacSandboxRunner used to build its permission profile
        // inline, so an operator's ToolOverrides["iac_plan"] never reached it. It now goes through
        // the shared resolver, so a DeniedCapabilities/MinimumIsolation override must show up on the
        // request the sandbox actually receives — here the deny (DatabaseRead) doesn't intersect
        // what iac_plan actually requires (FileRead|FileWrite|Subprocess|NetworkAccess), so the call
        // still dispatches with the deny carried on the profile.
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var services = new ServiceCollection();
        services.AddOptions<SandboxConfig>().Configure(c => c.ToolOverrides["iac_plan"] = new ToolOverrideConfig
        {
            DeniedCapabilities = ["DatabaseRead"],
            MinimumIsolation = "Container"
        });
        var provider = services.BuildServiceProvider();
        var resolver = new ToolPermissionProfileResolver(
            new FirstPartyToolLookup(provider, new HashSet<string>()),
            provider.GetRequiredService<IOptionsMonitor<SandboxConfig>>());

        await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            executor: sandbox,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            permissionResolver: resolver);

        var profile = sandbox.RequestFor("terraform").PermissionProfile;
        profile.DeniedCapabilities.Should().Be(ToolCapability.DatabaseRead);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container,
            "an operator-configured floor above Process must be honoured, not silently dropped");
    }

    [Fact]
    public async Task RunAsync_OperatorOverride_IntersectingDeny_RefusesOutrightWithoutDispatching()
    {
        // The #405 fix this cluster made everywhere else: a deny that intersects what the tool
        // actually requires must refuse the call outright, not silently narrow what gets
        // provisioned to the sandbox and let it run anyway (a code-review finding on the original
        // fix — IacSandboxRunner never calls ICapabilityEnforcer, so nothing else on this dispatch
        // path would have refused it).
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var services = new ServiceCollection();
        services.AddOptions<SandboxConfig>().Configure(c => c.ToolOverrides["iac_plan"] = new ToolOverrideConfig
        {
            DeniedCapabilities = ["NetworkAccess"]
        });
        var provider = services.BuildServiceProvider();
        var resolver = new ToolPermissionProfileResolver(
            new FirstPartyToolLookup(provider, new HashSet<string>()),
            provider.GetRequiredService<IOptionsMonitor<SandboxConfig>>());

        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            executor: sandbox,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            permissionResolver: resolver);

        result.Success.Should().BeFalse();
        sandbox.Requests.Should().BeEmpty(
            "a denied capability the tool actually requires must refuse before the sandbox is ever invoked");
    }
}
