using FluentAssertions;
using Infrastructure.AI.Iac;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Regression tests for the solution-review finding that the IaC sandbox egress
/// allowlist was never enforced: <see cref="IacSandboxRunner"/> declared
/// <c>AllowedHosts = registryAllowlist</c> but never populated
/// <c>SandboxExecutionRequest.EgressPrecheckTargets</c>, so the only sandbox-side
/// egress gate (the preflight, which short-circuits on an empty target list) never
/// ran. These tests assert the runner now surfaces every registry host as a
/// preflight target so the active egress policy is consulted before the CLI spawns.
/// </summary>
public sealed class IacSandboxRunnerSolutionReviewFixTests
{
    private const string ModuleDir = "/tmp/iac/module";

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
            toolName: "terraform_plan");

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
            toolName: "terraform_plan");

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
            toolName: "terraform_plan");

        var targets = sandbox.RequestFor("terraform").EgressPrecheckTargets!;
        targets.Select(u => u.Host).Should().ContainSingle().Which.Should().Be("registry.terraform.io");
    }
}
