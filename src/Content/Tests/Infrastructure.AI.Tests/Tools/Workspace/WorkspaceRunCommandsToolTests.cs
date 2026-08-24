using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tests.Support;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Unit tests for <see cref="WorkspaceRunTestsTool"/> and
/// <see cref="WorkspaceRunLintTool"/>. Both dispatch through the same
/// <see cref="WorkspaceCommandRunner"/> with different commands; the recording
/// fake executor lets us assert on the permission profile + command + working
/// directory passed to the sandbox.
/// </summary>
public sealed class WorkspaceRunCommandsToolTests
{
    [Fact]
    public async Task RunTests_DispatchesTestCommandThroughSandboxWithCorrectProfile()
    {
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test src/Solution.slnx");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "12 tests passed");
        var sut = new WorkspaceRunTestsTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync(
            "run",
            new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("12 tests passed");

        sandbox.Requests.Should().ContainSingle();
        var req = sandbox.Requests[0];
        req.Command.Should().Be("dotnet");
        req.ArgumentList.Should().NotBeNull().And.Equal("test", "src/Solution.slnx");

        // Permission profile sanity: no NetworkAccess capability (egress is deny-all for this skill).
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.NetworkAccess).Should().BeFalse();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.FileRead).Should().BeTrue();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.FileWrite).Should().BeTrue();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.Subprocess).Should().BeTrue();
        req.PermissionProfile.DeniedCapabilities.Should().Be(ToolCapability.None);
        req.PermissionProfile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Process,
            "the runner floors isolation at Process even with no operator override present (#405)");
        req.PermissionProfile.AllowedPrograms.Should().Contain("dotnet");
    }

    [Fact]
    public async Task RunTests_OperatorOverride_NonIntersectingDeny_ReachesTheRunner()
    {
        // Regression for #405's bypass gap: WorkspaceCommandRunner used to build its permission
        // profile inline, so an operator's ToolOverrides["run_tests"] never reached it. It now goes
        // through the shared resolver, so a DeniedCapabilities/MinimumIsolation override must show up
        // on the request the sandbox actually receives — here the deny (NetworkAccess) doesn't
        // intersect what run_tests actually requires (FileRead|FileWrite|Subprocess), so the call
        // still dispatches with the deny carried on the profile.
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "ok");
        var overrideConfig = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["run_tests"] = new ToolOverrideConfig
                {
                    DeniedCapabilities = ["NetworkAccess"],
                    MinimumIsolation = "Container"
                }
            }
        };
        var sut = new WorkspaceRunTestsTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox, overrideConfig), NullLogger<WorkspaceRunTestsTool>.Instance);

        await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        sandbox.Requests.Should().ContainSingle();
        var profile = sandbox.Requests[0].PermissionProfile;
        profile.DeniedCapabilities.Should().Be(ToolCapability.NetworkAccess);
        profile.MinimumIsolation.Should().Be(SandboxIsolationLevel.Container,
            "an operator-configured floor above Process must be honoured, not silently dropped");
    }

    [Fact]
    public async Task RunTests_OperatorOverride_IntersectingDeny_RefusesOutrightWithoutDispatching()
    {
        // The #405 fix this cluster made everywhere else: a deny that intersects what the tool
        // actually requires must refuse the call outright, not silently narrow what gets
        // provisioned to the sandbox and let it run anyway (a code-review finding on the original
        // fix — WorkspaceCommandRunner/IacSandboxRunner never call ICapabilityEnforcer, so nothing
        // else on this dispatch path would have refused it).
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "ok");
        var overrideConfig = new SandboxConfig
        {
            ToolOverrides = new()
            {
                ["run_tests"] = new ToolOverrideConfig { DeniedCapabilities = ["FileWrite"] }
            }
        };
        var sut = new WorkspaceRunTestsTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox, overrideConfig), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        sandbox.Requests.Should().BeEmpty(
            "a denied capability the tool actually requires must refuse before the sandbox is ever invoked");
    }

    [Fact]
    public async Task RunTests_NoTestCommand_RefusesWithoutInvokingSandbox()
    {
        using var fx = new WorkspaceTestFixture(testCommand: "");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "");
        var sut = new WorkspaceRunTestsTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("TestCommand");
        sandbox.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunTests_SandboxFails_SurfacesFailure()
    {
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var sandbox = new RecordingSandbox(success: false, exitCode: 1, output: "2 tests failed");
        var sut = new WorkspaceRunTestsTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("run_tests failed");
    }

    [Fact]
    public async Task RunLint_DispatchesLintCommandThroughSandbox()
    {
        using var fx = new WorkspaceTestFixture(lintCommand: "dotnet format --verify-no-changes");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "lint clean");
        var sut = new WorkspaceRunLintTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunLintTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        sandbox.Requests.Should().ContainSingle();
        sandbox.Requests[0].Command.Should().Be("dotnet");
        sandbox.Requests[0].ArgumentList.Should()
            .Equal("format", "--verify-no-changes");
    }

    [Fact]
    public async Task RunLint_NoLintCommand_Refuses()
    {
        using var fx = new WorkspaceTestFixture();
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "");
        var sut = new WorkspaceRunLintTool(fx.Accessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunLintTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        sandbox.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunTests_NoWorkspaceScope_Refuses()
    {
        var bareAccessor = new WorkspaceContextAccessor();
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "");
        var sut = new WorkspaceRunTestsTool(bareAccessor, TestScopeFactory.ForSandbox(sandbox), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("workspace context is active");
        sandbox.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunTests_NoExecutorRegisteredForResolvedIsolationTier_ReturnsFailureInsteadOfThrowing()
    {
        // #426 (mirrors #421's IacSandboxRunner fix): WorkspaceCommandRunner.RunAsync's try/catch must
        // cover ResolveExecutorForUngovernedDispatch's own executor lookup, not just
        // executor.ExecuteAsync — an operator's MinimumIsolation override can select a tier a template
        // consumer never registered a keyed ISandboxExecutor for at all.
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var sut = new WorkspaceRunTestsTool(
            fx.Accessor, TestScopeFactory.WithoutExecutors(), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse(
            "an unconfigured executor is a sandbox-level error — it must not throw out of ExecuteAsync uncaught");
    }

    [Fact]
    public async Task RunTests_ScopeFactoryHasNoPermissionResolverRegistered_ReturnsFailureInsteadOfThrowing()
    {
        // #426 (mirrors #421's IacSandboxRunner fix): scope creation and ToolPermissionProfileResolver
        // resolution used to happen in the caller, outside RunAsync's try/catch entirely, so a
        // DI-resolution failure there threw uncaught out of ITool.ExecuteAsync. RunAsync now creates
        // the scope and resolves the resolver itself, inside the same try/catch as dispatch/execution.
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var servicesWithNoResolver = new ServiceCollection().BuildServiceProvider();
        var sut = new WorkspaceRunTestsTool(
            fx.Accessor,
            servicesWithNoResolver.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse(
            "a DI-resolution failure for ToolPermissionProfileResolver is a sandbox-level error — it " +
            "must not throw out of ExecuteAsync uncaught");
    }

    /// <summary>
    /// Regression test, found by /code-review on the #425/#434 PR: a custom
    /// <see cref="ISandboxExecutor"/> (a template extensibility seam) violating its own
    /// non-nullable <c>ExecuteAsync</c> contract used to reach
    /// <c>WorkspaceCommandRunner.DispatchAsync</c>'s unguarded <c>sandboxResult.ExitCode</c>
    /// dereference as an unhandled <see cref="NullReferenceException"/>, caught only by
    /// <c>RunAsync</c>'s outer catch and degraded to a generic message rather than the specific,
    /// stable failure this tool's own dispatch-time faults otherwise avoid leaking.
    /// </summary>
    /// <remarks>
    /// Asserts on the exception type name in <see cref="ToolResult.Error"/>, not just
    /// <c>Success == false</c> — /simplify's altitude pass on this same PR found the weaker
    /// assertion would pass identically even with the guard reverted, since
    /// <c>WorkspaceCommandRunner.RunAsync</c>'s pre-existing outer catch already converts a bare
    /// <see cref="NullReferenceException"/> into a failed <see cref="ToolResult"/> too. Asserting
    /// the specific exception name proves the guard, not just the pre-existing catch-all.
    /// </remarks>
    [Fact]
    public async Task RunTests_ExecutorReturnsNull_ReturnsFailureInsteadOfThrowing()
    {
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");
        var sut = new WorkspaceRunTestsTool(
            fx.Accessor, TestScopeFactory.ForSandbox(new NullReturningSandboxExecutor()), NullLogger<WorkspaceRunTestsTool>.Instance);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse(
            "a sandbox executor violating its non-nullable contract must not throw out of ExecuteAsync uncaught");
        result.Error.Should().Contain(nameof(InvalidOperationException),
            "the guard's thrown exception type must reach the caller-visible failure, not just any exception " +
            "the pre-existing outer catch happened to also handle");
    }

    /// <summary>
    /// Recording fake <see cref="ISandboxExecutor"/>. Captures every
    /// <see cref="SandboxExecutionRequest"/> for assertion and returns a
    /// preconfigured result. No actual process is spawned.
    /// </summary>
    private sealed class RecordingSandbox : ISandboxExecutor
    {
        private readonly bool _success;
        private readonly int _exitCode;
        private readonly string _output;

        public RecordingSandbox(bool success, int exitCode, string output)
        {
            _success = success;
            _exitCode = exitCode;
            _output = output;
        }

        public List<SandboxExecutionRequest> Requests { get; } = new();

        public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new SandboxExecutionResult
            {
                Success = _success,
                ExitCode = _exitCode,
                Output = _output,
                ErrorMessage = _success ? null : _output
            });
        }
    }
}
