using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
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
        var sut = new WorkspaceRunTestsTool(fx.Accessor, sandbox);

        var result = await sut.ExecuteAsync(
            "run",
            new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("12 tests passed");

        sandbox.Requests.Should().ContainSingle();
        var req = sandbox.Requests[0];
        req.Command.Should().Be("dotnet");
        req.ArgumentList.Should().NotBeNull().And.Equal("test", "src/Solution.slnx");

        // Permission profile sanity: no NetworkAccess capability (egress is deny-all for this skill);
        // only the working copy is allow-listed.
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.NetworkAccess).Should().BeFalse();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.FileRead).Should().BeTrue();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.FileWrite).Should().BeTrue();
        req.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.Subprocess).Should().BeTrue();
        req.PermissionProfile.AllowedHosts.Should().BeEmpty();
        req.PermissionProfile.AllowedPaths.Should().ContainSingle()
            .Which.Should().Be(fx.Context.WorkingCopyPath);
        req.PermissionProfile.AllowedPrograms.Should().Contain("dotnet");
    }

    [Fact]
    public async Task RunTests_NoTestCommand_RefusesWithoutInvokingSandbox()
    {
        using var fx = new WorkspaceTestFixture(testCommand: "");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "");
        var sut = new WorkspaceRunTestsTool(fx.Accessor, sandbox);

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
        var sut = new WorkspaceRunTestsTool(fx.Accessor, sandbox);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("run_tests failed");
    }

    [Fact]
    public async Task RunLint_DispatchesLintCommandThroughSandbox()
    {
        using var fx = new WorkspaceTestFixture(lintCommand: "dotnet format --verify-no-changes");
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "lint clean");
        var sut = new WorkspaceRunLintTool(fx.Accessor, sandbox);

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
        var sut = new WorkspaceRunLintTool(fx.Accessor, sandbox);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        sandbox.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunTests_NoWorkspaceScope_Refuses()
    {
        var bareAccessor = new WorkspaceContextAccessor();
        var sandbox = new RecordingSandbox(success: true, exitCode: 0, output: "");
        var sut = new WorkspaceRunTestsTool(bareAccessor, sandbox);

        var result = await sut.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("workspace context is active");
        sandbox.Requests.Should().BeEmpty();
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
