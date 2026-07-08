using System.Security.Cryptography;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.Planner.StepExecutors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner.StepExecutors;

/// <summary>
/// Security regression coverage for the live attestation verification path (audit A6-2
/// review follow-up): <see cref="ToolUseStepExecutor"/> called signature-only
/// <c>VerifyAsync</c>, which never re-hashes the returned output — so a
/// <c>SandboxExecutionResult.Output</c> tampered after signing still verified. These tests
/// use the REAL <see cref="HmacAttestationService"/> so the tamper scenario is genuine:
/// the signature is valid, only the output diverges.
/// </summary>
public sealed class ToolUseStepExecutorAttestationBindingTests
{
    private readonly Mock<ICapabilityEnforcer> _capabilityEnforcer = new();
    private readonly Mock<ICompositeResponseSanitizer> _responseSanitizer = new();
    private readonly Mock<IPlanProgressNotifier> _notifier = new();
    private readonly Mock<ISandboxExecutor> _sandboxExecutor = new();
    private readonly HmacAttestationService _attestationService;
    private readonly ToolUseStepExecutor _sut;

    public ToolUseStepExecutorAttestationBindingTests()
    {
        _notifier.Setup(n => n.NotifySandboxStatusAsync(
                It.IsAny<PlanId>(), It.IsAny<PlanStepId>(), It.IsAny<string>(), It.IsAny<SandboxIsolationLevel>(),
                It.IsAny<ResourceUsage>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capabilityEnforcer.Setup(c => c.ResolveProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolPermissionProfile { RequiredCapabilities = ToolCapability.FileRead });

        _responseSanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((content, _) => SanitizationResult.Clean(content));

        var keyOptions = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v1",
            HmacKeys =
            [
                new HmacKeyEntry
                {
                    Version = "v1",
                    Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }
            ]
        };
        _attestationService = new HmacAttestationService(
            Mock.Of<IOptionsMonitor<AttestationKeyOptions>>(m => m.CurrentValue == keyOptions),
            TimeProvider.System,
            NullLogger<HmacAttestationService>.Instance);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, _sandboxExecutor.Object);
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Container, _sandboxExecutor.Object);

        _sut = new ToolUseStepExecutor(
            _capabilityEnforcer.Object,
            services.BuildServiceProvider(),
            _attestationService,
            _responseSanitizer.Object,
            _notifier.Object,
            new PlanExecutionContext { CurrentPlanId = new PlanId(Guid.NewGuid()) },
            NullLogger<ToolUseStepExecutor>.Instance);
    }

    private static PlanStep CreateStep() => new()
    {
        Id = new PlanStepId(Guid.NewGuid()),
        Name = "tool-step",
        Type = StepType.ToolUse,
        Configuration = new ToolUseConfig { ToolName = "file_system" },
        RetryPolicy = new RetryPolicy()
    };

    [Fact]
    public async Task ExecuteAsync_OutputTamperedAfterSigning_ReturnsFailed()
    {
        // A valid signature over "genuine-output" — then the result's Output diverges.
        var attestation = await _attestationService.SignAsync(
            "file_system", "{}", "genuine-output", CancellationToken.None);

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = true,
                Output = "tampered-output",
                Attestation = attestation,
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(CreateStep(), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Contains("Attestation verification failed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_OutputMatchesSignedBytes_ReturnsCompleted()
    {
        var attestation = await _attestationService.SignAsync(
            "file_system", "{}", "genuine-output", CancellationToken.None);

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = true,
                Output = "genuine-output",
                Attestation = attestation,
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(CreateStep(), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Completed, result.Status);
        Assert.Equal("genuine-output", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_LegacyOutputlessFailureAttestation_FallsBackToSignatureVerification()
    {
        // Output-less failure attestations (timeout, spawn refusal) have no OutputHash to
        // bind against — signature-only verification still applies, and the step reports
        // the sandbox failure, not an attestation failure.
        var attestation = await _attestationService.SignFailureAsync(
            "file_system", "{}", "Process timed out", CancellationToken.None);

        _sandboxExecutor.Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = "Process timed out",
                Attestation = attestation,
                ResourceUsage = new ResourceUsage()
            });

        var result = await _sut.ExecuteAsync(CreateStep(), new Dictionary<PlanStepId, string>(), CancellationToken.None);

        Assert.Equal(StepExecutionStatus.Failed, result.Status);
        Assert.Equal("Process timed out", result.ErrorMessage);
    }
}
