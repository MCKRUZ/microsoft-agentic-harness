using Application.AI.Common.Interfaces.Iac;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Iac;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config;
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

        var build = await Run(CliProgram, [ "build", MainFile, "--stdout" ], moduleDirectory, allowlist, "iac_plan", cancellationToken);
        if (build is null)
        {
            return Result<IacPlanResult>.Fail("iac.plan.sandbox_error");
        }

        if (!build.Success)
        {
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

        var armTtk = await Run(ArmTtkProgram, [ "-TemplatePath", "." ], moduleDirectory, iac.RegistryAllowlist, "iac_scan", cancellationToken);
        var checkov = await Run(CheckovProgram, [ "-d", ".", "--compact", "--quiet" ], moduleDirectory, iac.RegistryAllowlist, "iac_scan", cancellationToken);
        if (armTtk is null || checkov is null)
        {
            return Result<IacScanResult>.Fail("iac.scan.sandbox_error");
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
        CancellationToken cancellationToken)
    {
        try
        {
            // The executor is SCOPED — resolve it from a fresh scope per run
            // so this singleton generator never captures scope-bound state.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sandbox = scope.ServiceProvider.GetRequiredKeyedService<ISandboxExecutor>(_isolationLevel);

            return await IacSandboxRunner.RunAsync(program, args, moduleDirectory, allowlist, sandbox, toolName, cancellationToken: cancellationToken);
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
