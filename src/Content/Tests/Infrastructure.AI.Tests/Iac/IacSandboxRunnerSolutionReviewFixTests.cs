using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Iac;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Infrastructure.AI.Tests.Support;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Builds an <see cref="IServiceScopeFactory"/> whose scopes resolve <paramref name="sandbox"/> as
    /// the keyed <see cref="ISandboxExecutor"/> under both production-keyed isolation tiers (Process
    /// and Container — <see cref="IacSandboxRunner.RunAsync"/> resolves the executor for whichever
    /// tier the resolved profile's <c>MinimumIsolation</c> lands on) and a
    /// <see cref="ToolPermissionProfileResolver"/> carrying the given override, or no override at all
    /// when <paramref name="overrideToolName"/> is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors what the real DI container provides <see cref="IacSandboxRunner.RunAsync"/> via its own
    /// <c>scopeFactory</c> parameter — a code-review finding (3rd occurrence on this method): RunAsync
    /// used to receive an already-created scope's provider and an already-resolved resolver, so scope
    /// creation and resolver lookup happened in the caller, outside RunAsync's own try/catch. Two prior
    /// fixes widened that try/catch (first to cover dispatch resolution, then nothing further), but the
    /// generator-side scope/resolver setup stayed unprotected until RunAsync took over owning the whole
    /// scope lifecycle itself, via a scope factory instead of a pre-built provider.
    /// </remarks>
    private static IServiceScopeFactory ScopeFactory(
        ISandboxExecutor sandbox, string? overrideToolName = null, ToolOverrideConfig? overrideConfig = null)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(SandboxIsolationLevel.Process, sandbox);
        services.AddKeyedSingleton(SandboxIsolationLevel.Container, sandbox);
        services.AddOptions<SandboxConfig>().Configure(c =>
        {
            if (overrideToolName is not null && overrideConfig is not null)
                c.ToolOverrides[overrideToolName] = overrideConfig;
        });
        services.AddSingleton(sp => new FirstPartyToolLookup(sp, new HashSet<string>()));
        services.AddSingleton<ToolPermissionProfileResolver>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
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
            scopeFactory: ScopeFactory(sandbox),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

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
            scopeFactory: ScopeFactory(sandbox),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

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
            scopeFactory: ScopeFactory(sandbox),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

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

        await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: ScopeFactory(sandbox, "iac_plan", new ToolOverrideConfig
            {
                DeniedCapabilities = ["DatabaseRead"],
                MinimumIsolation = "Container"
            }),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

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

        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: ScopeFactory(sandbox, "iac_plan", new ToolOverrideConfig
            {
                DeniedCapabilities = ["NetworkAccess"]
            }),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden,
            "a pre-dispatch governance refusal must be a structurally distinct outcome (#421), not a " +
            "look-alike SandboxExecutionResult a caller has to reinterpret");
        sandbox.Requests.Should().BeEmpty(
            "a denied capability the tool actually requires must refuse before the sandbox is ever invoked");
    }

    [Fact]
    public async Task RunAsync_DispatchSucceeds_ReturnsSuccessfulResultEvenWhenTheCliItselfFails()
    {
        // The other half of #421's guarantee: a genuine CLI failure (the executor actually ran and
        // reported failure) must come back as a *successful* dispatch carrying a failed
        // SandboxExecutionResult — never conflated with the sandbox refusing to dispatch at all.
        var sandbox = new RecordingIacSandbox().WithDefault(false, 1, "terraform: syntax error");

        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["validate"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: ScopeFactory(sandbox),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

        result.IsSuccess.Should().BeTrue("a genuine CLI failure is a completed dispatch, not a refusal");
        result.Value!.Success.Should().BeFalse();
        result.Value.ExitCode.Should().Be(1);
        sandbox.Requests.Should().ContainSingle(
            "the CLI must actually have been invoked for this to be a real (not refused) failure");
    }

    [Fact]
    public async Task RunAsync_NoExecutorRegisteredForResolvedIsolationTier_ReturnsGeneralFailureInsteadOfThrowing()
    {
        // #421 follow-up (code-review finding): RunAsync's try/catch must cover
        // ResolveExecutorForUngovernedDispatch's own executor lookup, not just executor.ExecuteAsync —
        // an operator's MinimumIsolation override can select a tier a template consumer never
        // registered a keyed ISandboxExecutor for at all.
        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: TestScopeFactory.WithoutExecutors(),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General,
            "an unconfigured executor is a sandbox-level error, not a governance refusal — it must not " +
            "throw out of RunAsync uncaught");
    }

    [Fact]
    public async Task RunAsync_ScopeFactoryHasNoPermissionResolverRegistered_ReturnsGeneralFailureInsteadOfThrowing()
    {
        // #421 follow-up (code-review finding, 3rd occurrence on this method): the generators' own
        // scope-creation and ToolPermissionProfileResolver lookup used to happen outside RunAsync
        // entirely, so a DI-resolution failure there threw uncaught out of PlanAsync/ScanAsync and
        // reached SelfValidationGate as a raw, unscrubbed exception message. RunAsync now creates the
        // scope and resolves the resolver itself, inside the same try/catch as dispatch and execution.
        var servicesWithNoResolver = new ServiceCollection().BuildServiceProvider();

        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["plan"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: servicesWithNoResolver.GetRequiredService<IServiceScopeFactory>(),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "iac_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.General,
            "a DI-resolution failure for ToolPermissionProfileResolver is a sandbox-level error, not a " +
            "governance refusal — it must not throw out of RunAsync uncaught");
    }

    [Fact]
    public void MapDispatchFailure_ForbiddenDispatch_PreservesForbiddenFailureType()
    {
        // #421 follow-up (code-review finding): MapDispatchFailure used to collapse a Forbidden
        // dispatch to Result<T>.Fail (FailureType.General) on the way out — discarding the very
        // distinction RunAsync had just established, at the one boundary this issue exists to keep
        // it distinct across.
        var forbidden = Result<SandboxExecutionResult>.Forbidden("denied");

        var mapped = IacSandboxRunner.MapDispatchFailure<IacPlanResult>(
            forbidden, NullLogger.Instance, "Terraform", "iac_plan", ModuleDir, codePrefix: "iac.plan");

        mapped.Should().NotBeNull();
        mapped!.FailureType.Should().Be(ResultFailureType.Forbidden);
        mapped.Errors.Should().Contain("iac.plan.sandbox_denied");
    }
}
