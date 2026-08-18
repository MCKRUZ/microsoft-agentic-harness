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
/// Bicep <see cref="IIacGenerator"/>. Scaffolds a starter <c>main.bicep</c>
/// deterministically, validates it with <c>bicep build</c>, and security-scans it
/// with ARM-TTK + Checkov — all CLI work runs inside the PR-3 sandbox via
/// <see cref="IacSandboxRunner"/>. Never deploys: there is no apply.
/// </summary>
/// <remarks>
/// <para>
/// <c>bicep build</c> compiles the template to ARM JSON; it surfaces syntax and
/// semantic errors but does not compute a resource diff, so a successful build is
/// reported as <see cref="IacPlanResult.Succeeded"/> with no change / destruction
/// signal. The real what-if diff happens at apply time, which this skill never
/// performs.
/// </para>
/// <para>
/// Stable failure codes (<c>iac.*</c>): raw CLI stderr is logged via structured
/// logging and never returned in a <see cref="Result"/> error, so a credential in
/// a provider error can never leak into LLM context.
/// </para>
/// </remarks>
public sealed class BicepGenerator : IIacGenerator
{
    private const string CliProgram = "bicep";
    private const string ArmTtkProgram = "arm-ttk";
    private const string CheckovProgram = "checkov";
    private const string MainFile = "main.bicep";

    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SandboxIsolationLevel _isolationLevel;
    private readonly ILogger<BicepGenerator> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new <see cref="BicepGenerator"/>.</summary>
    /// <param name="config">Application configuration monitor — supplies version pins, registry allowlist, and blocking severity.</param>
    /// <param name="scopeFactory">Scope factory used to resolve the keyed-SCOPED <see cref="ISandboxExecutor"/> per CLI run.
    /// The generator is a keyed SINGLETON, so a construction-time executor would be a captive dependency
    /// that scope validation rejects and that shares scoped state across requests.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="timeProvider">Clock abstraction (injected for parity and future use).</param>
    /// <param name="isolationLevel">The sandbox isolation level to resolve the executor for. Defaults to <see cref="SandboxIsolationLevel.Process"/>.</param>
    public BicepGenerator(
        IOptionsMonitor<AppConfig> config,
        IServiceScopeFactory scopeFactory,
        ILogger<BicepGenerator> logger,
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
    public IacBackend Backend => IacBackend.Bicep;

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
            [MainFile] = BuildMainBicep(request)
        };

        return Task.FromResult(Result<IacGenerationResult>.Success(new IacGenerationResult
        {
            Backend = IacBackend.Bicep,
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

        var build = await Run(
            CliProgram, [ "build", MainFile, "--stdout" ], moduleDirectory, allowlist, "iac_plan",
            IacPlanTool.RequiredSandboxCapabilities, cancellationToken);
        if (build is null)
        {
            return Result<IacPlanResult>.Fail("iac.plan.sandbox_error");
        }

        if (!build.Success)
        {
            if (build.Attestation is null)
            {
                // The sandbox never dispatched bicep at all — a governance refusal (e.g. an operator's
                // DeniedCapabilities override on iac_plan), not a real build failure. A code-review
                // finding: this used to fall straight through to the IacPlanResult below and report
                // Succeeded=false/"bicep build failed" — a governance denial silently presenting as a
                // template syntax error, with the real reason (build.ErrorMessage) never logged or
                // surfaced anywhere. Log it via structured logging (never the raw message in a Result
                // error — see this class's remarks on why) and fail loudly instead.
                // Discriminated on Attestation, not ExitCode (a code-review finding on the first cut of
                // this fix): both ProcessSandboxExecutor and DockerSandboxExecutor sign a failure
                // attestation on every genuinely-dispatched outcome — timeout, reserved-env-grant
                // rejection, egress-preflight block, and a real crash all leave ExitCode null too, but
                // all of them sign one. Only the pre-dispatch refusal branch in IacSandboxRunner never
                // reaches an executor at all, so it's the one case Attestation is reliably null for.
                _logger.LogError(
                    "Bicep iac_plan for {Module} was refused before dispatch: {Reason}",
                    moduleDirectory, build.ErrorMessage);
                return Result<IacPlanResult>.Fail("iac.plan.sandbox_denied");
            }

            _logger.LogWarning("Bicep build failed in {Module}: exit={Exit}", moduleDirectory, build.ExitCode);
        }

        return Result<IacPlanResult>.Success(new IacPlanResult
        {
            Backend = IacBackend.Bicep,
            ModulePath = moduleDirectory,
            Succeeded = build.Success,
            HasChanges = false,
            HasDestructiveChanges = false,
            RawOutput = build.Output ?? string.Empty,
            Summary = build.Success ? "bicep build succeeded" : "bicep build failed"
        });
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

        var armTtk = await Run(
            ArmTtkProgram, [ "-TemplatePath", "." ], moduleDirectory, iac.RegistryAllowlist, "iac_scan",
            IacScanTool.RequiredSandboxCapabilities, cancellationToken);
        var checkov = await Run(
            CheckovProgram, [ "-d", ".", "--compact", "--quiet" ], moduleDirectory, iac.RegistryAllowlist, "iac_scan",
            IacScanTool.RequiredSandboxCapabilities, cancellationToken);
        if (armTtk is null || checkov is null)
        {
            return Result<IacScanResult>.Fail("iac.scan.sandbox_error");
        }

        // A code-review finding: a scanner the sandbox refused to dispatch (no signed attestation —
        // e.g. a governance denial on iac_scan) used to fall straight through to the parsers below,
        // which parse empty output into zero findings — silently reporting a security scan that never
        // ran as "passed, no findings." Refuse loudly instead of reporting a false clean result.
        // Discriminated on Attestation, not ExitCode (a code-review finding on the first cut of this
        // fix): every genuinely-dispatched outcome — including timeout, a reserved-env-grant rejection,
        // an egress-preflight block, and a real crash — signs a failure attestation and also leaves
        // ExitCode null, so ExitCode alone would have misclassified those as refusals too. Only the
        // pre-dispatch refusal branch in IacSandboxRunner never reaches an executor, so Attestation is
        // the one field reliably null for that case alone.
        if (armTtk.Attestation is null)
        {
            _logger.LogError(
                "Bicep iac_scan (arm-ttk) for {Module} was refused before dispatch: {Reason}",
                moduleDirectory, armTtk.ErrorMessage);
            return Result<IacScanResult>.Fail("iac.scan.sandbox_denied");
        }
        if (checkov.Attestation is null)
        {
            _logger.LogError(
                "Bicep iac_scan (checkov) for {Module} was refused before dispatch: {Reason}",
                moduleDirectory, checkov.ErrorMessage);
            return Result<IacScanResult>.Fail("iac.scan.sandbox_denied");
        }

        var findings = new List<IacScanFinding>();
        findings.AddRange(ArmTtkParser.Parse(armTtk.Output ?? string.Empty));
        findings.AddRange(CheckovParser.Parse(checkov.Output ?? string.Empty));

        return Result<IacScanResult>.Success(new IacScanResult
        {
            Backend = IacBackend.Bicep,
            ModulePath = moduleDirectory,
            Passed = IacScanSeverityParser.Passes(findings, blocking),
            ScannersRun = [ArmTtkProgram, CheckovProgram],
            Findings = findings
        });
    }

    private async Task<SandboxExecutionResult?> Run(
        string program,
        IReadOnlyList<string> args,
        string moduleDirectory,
        IReadOnlyList<string> allowlist,
        string toolName,
        ToolCapability requiredCapabilities,
        CancellationToken cancellationToken)
    {
        try
        {
            // The executor is SCOPED — resolve it from a fresh scope per run
            // so this singleton generator never captures scope-bound state. Resolved inside
            // RunAsync, after the profile, so an operator's MinimumIsolation override actually
            // selects the executor.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var permissionResolver = scope.ServiceProvider.GetRequiredService<ToolPermissionProfileResolver>();

            return await IacSandboxRunner.RunAsync(
                program, args, moduleDirectory, allowlist, scope.ServiceProvider, _isolationLevel,
                toolName, requiredCapabilities, permissionResolver, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bicep sandbox run failed for {Program} in {Module}.", program, moduleDirectory);
            return null;
        }
    }

    private static string BuildMainBicep(IacGenerationRequest request)
    {
        var properties = string.Join(
            "\n",
            request.Parameters.Select(p => $"    {p.Key}: '{p.Value}'"));
        var body = string.IsNullOrEmpty(properties) ? string.Empty : properties + "\n";

        return $$"""
            // Scaffolded by the Microsoft Agentic Harness IaC skill (Bicep).
            param environment string = '{{request.Environment}}'

            resource {{request.ResourceName}} '{{request.ResourceType}}@2023-01-01' = {
              name: '{{request.ResourceName}}'
              tags: {
                environment: environment
                managedBy: 'agentic-harness'
              }
              properties: {
            {{body}}  }
            }
            """;
    }
}
