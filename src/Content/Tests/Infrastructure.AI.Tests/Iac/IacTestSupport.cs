using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Iac;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Shared builders for the IaC test suite: an <see cref="IOptionsMonitor{AppConfig}"/>
/// whose <c>AI.Iac</c> section is configured per scenario, and a programmable
/// recording sandbox that returns canned CLI output keyed by the program invoked.
/// </summary>
internal static class IacTestConfig
{
    /// <summary>Builds an <see cref="AppConfig"/> with a fully-valid IaC section.</summary>
    public static AppConfig ValidAppConfig(
        string blockingSeverity = "High",
        IEnumerable<string>? enabledBackends = null,
        IEnumerable<string>? registryAllowlist = null)
    {
        return new AppConfig
        {
            AI = new AIConfig
            {
                Iac = new IacConfig
                {
                    Enabled = true,
                    EnabledBackends = (enabledBackends ?? ["terraform", "bicep"]).ToList(),
                    BlockingSeverity = blockingSeverity,
                    RegistryAllowlist = (registryAllowlist ?? ["registry.terraform.io", "mcr.microsoft.com"]).ToList()
                }
            }
        };
    }

    /// <summary>Wraps an <see cref="AppConfig"/> in a Moq-backed monitor.</summary>
    public static IOptionsMonitor<AppConfig> Monitor(AppConfig appConfig)
        => Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

    /// <summary>Convenience: a monitor over a valid config with the given blocking severity.</summary>
    public static IOptionsMonitor<AppConfig> ValidMonitor(string blockingSeverity = "High")
        => Monitor(ValidAppConfig(blockingSeverity));
}

/// <summary>
/// Recording <see cref="ISandboxExecutor"/> fake that returns canned results keyed
/// by the CLI program (<c>terraform</c>, <c>bicep</c>, <c>checkov</c>, <c>tfsec</c>,
/// <c>arm-ttk</c>). Records every request so tests can assert the exact program +
/// argument list the generator built.
/// </summary>
internal sealed class RecordingIacSandbox : ISandboxExecutor
{
    private readonly Dictionary<string, SandboxExecutionResult> _byProgram = new(StringComparer.OrdinalIgnoreCase);
    private SandboxExecutionResult _default = new() { Success = true, ExitCode = 0, Output = string.Empty };

    /// <summary>Every request the generator dispatched, in order.</summary>
    public List<SandboxExecutionRequest> Requests { get; } = [];

    /// <summary>Programs invoked, in order — convenience accessor for assertions.</summary>
    public IReadOnlyList<string?> Programs => Requests.Select(r => r.Command).ToList();

    /// <summary>Sets the canned result for a given program (matched case-insensitively).</summary>
    public RecordingIacSandbox ForProgram(string program, bool success, int exitCode, string output)
    {
        _byProgram[program] = new SandboxExecutionResult
        {
            Success = success,
            ExitCode = exitCode,
            Output = output,
            ErrorMessage = success ? null : output
        };
        return this;
    }

    /// <summary>Sets the fallback result for any program with no explicit mapping.</summary>
    public RecordingIacSandbox WithDefault(bool success, int exitCode, string output)
    {
        _default = new SandboxExecutionResult
        {
            Success = success,
            ExitCode = exitCode,
            Output = output,
            ErrorMessage = success ? null : output
        };
        return this;
    }

    /// <summary>The single request whose program matches <paramref name="program"/>.</summary>
    public SandboxExecutionRequest RequestFor(string program)
        => Requests.Single(r => string.Equals(r.Command, program, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var result = _byProgram.TryGetValue(request.Command ?? string.Empty, out var mapped) ? mapped : _default;
        return Task.FromResult(result);
    }
}
