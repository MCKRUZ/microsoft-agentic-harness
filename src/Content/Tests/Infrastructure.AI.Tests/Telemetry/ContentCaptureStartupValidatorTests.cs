using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Telemetry;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="ContentCaptureStartupValidator"/>. The hosted service
/// no-ops when content-capture is off; when on it fails-loud unless the OTel
/// semconv-stability env var is pinned to the expected value AND an
/// <see cref="IContentRedactionFilter"/> is registered. The env var is global
/// process state, so each test that touches it restores the prior value in a
/// <c>finally</c>.
/// </summary>
[Collection("ContentCaptureEnvVar")]
public sealed class ContentCaptureStartupValidatorTests
{
    private static ContentCaptureStartupValidator Build(
        ContentCaptureConfig capture,
        bool registerFilter)
    {
        var services = new ServiceCollection();
        if (registerFilter)
        {
            services.AddSingleton<IContentRedactionFilter, DefaultContentRedactionFilter>();
        }
        var sp = services.BuildServiceProvider();

        return new ContentCaptureStartupValidator(
            sp,
            ContentCaptureTestConfig.Monitor(capture),
            NullLogger<ContentCaptureStartupValidator>.Instance);
    }

    private static async Task WithEnvVar(string? value, Func<Task> body)
    {
        var prior = Environment.GetEnvironmentVariable(GenAiSemconvRegistry.SemconvStabilityOptInEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GenAiSemconvRegistry.SemconvStabilityOptInEnvVar, value);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GenAiSemconvRegistry.SemconvStabilityOptInEnvVar, prior);
        }
    }

    [Fact]
    public async Task StartAsync_Disabled_NoOpEvenWithoutEnvVarOrFilter()
    {
        var validator = Build(new ContentCaptureConfig { Enabled = false }, registerFilter: false);

        await WithEnvVar(null, async () =>
        {
            var act = async () => await validator.StartAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        });
    }

    [Fact]
    public async Task StartAsync_EnabledButEnvVarUnset_Throws()
    {
        var validator = Build(ContentCaptureTestConfig.AllOn(), registerFilter: true);

        await WithEnvVar(null, async () =>
        {
            var act = async () => await validator.StartAsync(CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*{GenAiSemconvRegistry.SemconvStabilityOptInEnvVar}*");
        });
    }

    [Fact]
    public async Task StartAsync_EnabledButEnvVarWrongValue_Throws()
    {
        var validator = Build(ContentCaptureTestConfig.AllOn(), registerFilter: true);

        await WithEnvVar("stable", async () =>
        {
            var act = async () => await validator.StartAsync(CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
        });
    }

    [Fact]
    public async Task StartAsync_EnabledAndEnvVarPinnedButNoFilter_Throws()
    {
        var validator = Build(ContentCaptureTestConfig.AllOn(), registerFilter: false);

        await WithEnvVar(GenAiSemconvRegistry.SemconvStabilityOptInValue, async () =>
        {
            var act = async () => await validator.StartAsync(CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*IContentRedactionFilter*");
        });
    }

    [Fact]
    public async Task StartAsync_EnabledAndEnvVarPinnedAndFilterRegistered_DoesNotThrow()
    {
        var validator = Build(ContentCaptureTestConfig.AllOn(), registerFilter: true);

        await WithEnvVar(GenAiSemconvRegistry.SemconvStabilityOptInValue, async () =>
        {
            var act = async () => await validator.StartAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        });
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var validator = Build(new ContentCaptureConfig { Enabled = false }, registerFilter: false);

        var act = async () => await validator.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}

/// <summary>
/// Serializes tests that mutate the process-global
/// <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> environment variable so they do not
/// interfere with one another when xUnit runs collections in parallel.
/// </summary>
[CollectionDefinition("ContentCaptureEnvVar", DisableParallelization = true)]
public sealed class ContentCaptureEnvVarCollection;
