using Application.AI.Common.Interfaces.Iac;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Iac;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Terraform <see cref="IIacGenerator"/>. Scaffolds a starter module
/// deterministically, validates + plans it with the <c>terraform</c> CLI, and
/// security-scans it with Checkov + tfsec — all CLI work runs inside the PR-3
/// sandbox via <see cref="IacSandboxRunner"/>. Never deploys: there is no apply.
/// </summary>
/// <remarks>
/// <para>
/// Stable failure codes (<c>iac.*</c>): the implementation never surfaces raw CLI
/// stderr in a <see cref="Result"/> error — it logs the full output via structured
/// logging and returns a scrubbed code so a SAS token or registry credential in a
/// provider error can never leak into LLM context or an audit line.
/// </para>
/// </remarks>
public sealed class TerraformGenerator : IIacGenerator
{
    private const string CliProgram = "terraform";
    private const string CheckovProgram = "checkov";
    private const string TfsecProgram = "tfsec";

    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SandboxIsolationLevel _isolationLevel;
    private readonly ILogger<TerraformGenerator> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="TerraformGenerator"/>.</summary>
    /// <param name="config">Application configuration monitor — supplies version pins, registry allowlist, and blocking severity.</param>
    /// <param name="scopeFactory">Scope factory used to resolve the keyed-SCOPED <see cref="ISandboxExecutor"/> per CLI run.
    /// The generator is a keyed SINGLETON, so a construction-time executor would be a captive dependency
    /// that scope validation rejects and that shares scoped state across requests.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="timeProvider">Clock abstraction (unused for timing here but injected for parity and future use).</param>
    /// <param name="isolationLevel">The sandbox isolation level to resolve the executor for. Defaults to <see cref="SandboxIsolationLevel.Process"/>.</param>
    public TerraformGenerator(
        IOptionsMonitor<AppConfig> config,
        IServiceScopeFactory scopeFactory,
        ILogger<TerraformGenerator> logger,
        TimeProvider timeProvider,
        SandboxIsolationLevel isolationLevel = SandboxIsolationLevel.Process)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _config = config;
        _scopeFactory = scopeFactory;
        _isolationLevel = isolationLevel;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public IacBackend Backend => IacBackend.Terraform;

    /// <inheritdoc />
    public Task<Result<IacGenerationResult>> GenerateAsync(
        IacGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ResourceType) || string.IsNullOrWhiteSpace(request.ResourceName))
        {
            return Task.FromResult(Result<IacGenerationResult>.Fail("iac.generate.invalid_request"));
        }

        var files = new Dictionary<string, string>
        {
            ["main.tf"] = BuildMainTf(request),
            ["variables.tf"] = BuildVariablesTf(request)
        };

        return Task.FromResult(Result<IacGenerationResult>.Success(new IacGenerationResult
        {
            Backend = IacBackend.Terraform,
            Files = files
        }));
    }

    /// <inheritdoc />
    public async Task<Result<IacPlanResult>> PlanAsync(string moduleDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moduleDirectory))
        {
            return Result<IacPlanResult>.Fail("iac.plan.invalid_module_directory");
        }

        var allowlist = _config.CurrentValue.AI.Iac.RegistryAllowlist;

        // A governance refusal (e.g. an operator's DeniedCapabilities override on iac_plan) must
        // never present as a real validate failure.
        var validateDispatch = await Run(
            [ "validate", "-no-color" ], moduleDirectory, allowlist, "iac_plan",
            IacPlanTool.RequiredSandboxCapabilities, cancellationToken);
        if (IacSandboxRunner.MapDispatchFailure<IacPlanResult>(
                validateDispatch, _logger, "Terraform", "iac_plan", moduleDirectory,
                "iac.plan.sandbox_denied", "iac.plan.sandbox_error") is { } refused)
        {
            return refused;
        }

        var validate = validateDispatch.Value!;
        if (!validate.Success)
        {
            _logger.LogWarning("Terraform validate failed in {Module}: exit={Exit}", moduleDirectory, validate.ExitCode);
            return Result<IacPlanResult>.Success(FailedPlan(moduleDirectory, validate.Output ?? string.Empty, "validate failed"));
        }

        // The same refusal-vs-real-failure ambiguity applies to `plan` too — a config-override reload
        // between the two calls could refuse this second dispatch even though validate succeeded.
        var planDispatch = await Run(
            [ "plan", "-no-color", "-detailed-exitcode" ], moduleDirectory, allowlist, "iac_plan",
            IacPlanTool.RequiredSandboxCapabilities, cancellationToken);
        if (IacSandboxRunner.MapDispatchFailure<IacPlanResult>(
                planDispatch, _logger, "Terraform", "iac_plan (plan)", moduleDirectory,
                "iac.plan.sandbox_denied", "iac.plan.sandbox_error") is { } refusedPlan)
        {
            return refusedPlan;
        }

        return Result<IacPlanResult>.Success(ParsePlan(moduleDirectory, planDispatch.Value!));
    }

    /// <inheritdoc />
    public async Task<Result<IacScanResult>> ScanAsync(string moduleDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moduleDirectory))
        {
            return Result<IacScanResult>.Fail("iac.scan.invalid_module_directory");
        }

        var iac = _config.CurrentValue.AI.Iac;
        if (!IacScanSeverityParser.TryParse(iac.BlockingSeverity, out var blocking))
        {
            return Result<IacScanResult>.Fail("iac.scan.invalid_blocking_severity");
        }

        // Independent dispatches — each opens its own DI scope and resolves its own executor, so
        // there is no shared mutable state and both can run concurrently.
        // A scanner the sandbox refused to dispatch must never fall through to the parsers below,
        // which would parse empty output into zero findings — silently reporting a security scan that
        // never ran as "passed, no findings."
        var checkovTask = Run(
            [ "-d", ".", "--compact", "--quiet" ], moduleDirectory, iac.RegistryAllowlist, "iac_scan",
            IacScanTool.RequiredSandboxCapabilities, cancellationToken, CheckovProgram);
        var tfsecTask = Run(
            [ ".", "--no-colour" ], moduleDirectory, iac.RegistryAllowlist, "iac_scan",
            IacScanTool.RequiredSandboxCapabilities, cancellationToken, TfsecProgram);
        await Task.WhenAll(checkovTask, tfsecTask);
        var checkovDispatch = checkovTask.Result;
        var tfsecDispatch = tfsecTask.Result;

        if (IacSandboxRunner.MapDispatchFailure<IacScanResult>(
                checkovDispatch, _logger, "Terraform", "iac_scan (checkov)", moduleDirectory,
                "iac.scan.sandbox_denied", "iac.scan.sandbox_error") is { } refusedCheckov)
        {
            return refusedCheckov;
        }
        if (IacSandboxRunner.MapDispatchFailure<IacScanResult>(
                tfsecDispatch, _logger, "Terraform", "iac_scan (tfsec)", moduleDirectory,
                "iac.scan.sandbox_denied", "iac.scan.sandbox_error") is { } refusedTfsec)
        {
            return refusedTfsec;
        }

        var checkov = checkovDispatch.Value!;
        var tfsec = tfsecDispatch.Value!;
        var findings = new List<IacScanFinding>();
        findings.AddRange(CheckovParser.Parse(checkov.Output ?? string.Empty));
        findings.AddRange(TfsecParser.Parse(tfsec.Output ?? string.Empty));

        return Result<IacScanResult>.Success(new IacScanResult
        {
            Backend = IacBackend.Terraform,
            ModulePath = moduleDirectory,
            Passed = IacScanSeverityParser.Passes(findings, blocking),
            ScannersRun = [CheckovProgram, TfsecProgram],
            Findings = findings
        });
    }

    private async Task<Result<SandboxExecutionResult>> Run(
        IReadOnlyList<string> args,
        string moduleDirectory,
        IReadOnlyList<string> allowlist,
        string toolName,
        ToolCapability requiredCapabilities,
        CancellationToken cancellationToken,
        string program = CliProgram)
    {
        // The executor is SCOPED — resolve it from a fresh scope per run
        // so this singleton generator never captures scope-bound state. Resolved inside
        // RunAsync, after the profile, so an operator's MinimumIsolation override actually
        // selects the executor.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var permissionResolver = scope.ServiceProvider.GetRequiredService<ToolPermissionProfileResolver>();

        return await IacSandboxRunner.RunAsync(
            program, args, moduleDirectory, allowlist, scope.ServiceProvider, _isolationLevel,
            toolName, requiredCapabilities, permissionResolver, _logger, "Terraform",
            cancellationToken: cancellationToken);
    }

    private static IacPlanResult ParsePlan(string moduleDirectory, SandboxExecutionResult plan)
    {
        // terraform plan -detailed-exitcode: 0 = no changes, 2 = changes present, 1 = error.
        var output = plan.Output ?? string.Empty;
        var hasChanges = plan.ExitCode == 2 || output.Contains("to add", StringComparison.OrdinalIgnoreCase);
        var destructive = output.Contains("to destroy", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("0 to destroy", StringComparison.OrdinalIgnoreCase);
        var succeeded = plan.ExitCode is 0 or 2;

        return new IacPlanResult
        {
            Backend = IacBackend.Terraform,
            ModulePath = moduleDirectory,
            Succeeded = succeeded,
            HasChanges = hasChanges,
            HasDestructiveChanges = destructive,
            RawOutput = output,
            Summary = succeeded ? PlanSummaryLine(output) : "plan errored"
        };
    }

    private static IacPlanResult FailedPlan(string moduleDirectory, string output, string summary) => new()
    {
        Backend = IacBackend.Terraform,
        ModulePath = moduleDirectory,
        Succeeded = false,
        HasChanges = false,
        HasDestructiveChanges = false,
        RawOutput = output,
        Summary = summary
    };

    private static string PlanSummaryLine(string output)
    {
        var line = output.Split('\n').LastOrDefault(l => l.Contains("to add", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(line) ? "no changes" : line.Trim();
    }

    private static string BuildMainTf(IacGenerationRequest request)
    {
        var attributes = string.Join(
            "\n",
            request.Parameters.Select(p => $"  {p.Key} = \"{p.Value}\""));
        var body = string.IsNullOrEmpty(attributes) ? string.Empty : attributes + "\n";

        return $$"""
            # Scaffolded by the Microsoft Agentic Harness IaC skill (Terraform).
            # Environment: {{request.Environment}}
            resource "{{request.ResourceType}}" "{{request.ResourceName}}" {
            {{body}}  tags = {
                environment = var.environment
                managed_by  = "agentic-harness"
              }
            }
            """;
    }

    private static string BuildVariablesTf(IacGenerationRequest request) => $$"""
        variable "environment" {
          description = "Target environment for the {{request.ResourceName}} resource."
          type        = string
          default     = "{{request.Environment}}"
        }
        """;
}
