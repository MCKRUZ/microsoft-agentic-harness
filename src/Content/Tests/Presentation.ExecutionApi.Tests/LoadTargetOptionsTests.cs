using FluentAssertions;
using Presentation.ExecutionApi.LoadTarget;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// The load target's command line must fail at start-up on anything it does not understand. An option
/// accepted silently keeps its default, and the mistake surfaces minutes later as a confusing load-run
/// failure (a stress ramp running out of callers, say) far from its cause.
/// </summary>
public sealed class LoadTargetOptionsTests
{
    [Fact]
    public void Parse_OnlyAnOutputFolder_UsesTheDocumentedDefaults()
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "fixtures"]);

        error.Should().BeNull();
        options!.Port.Should().Be(5080);
        options.Callers.Should().Be(200);
        options.Tenants.Should().Be(4);
        options.Model.Name.Should().Be("realistic");
        options.TokenLifetime.Should().Be(TimeSpan.FromMinutes(240));
        options.EnableToolInvocation.Should().BeFalse();
        options.WithTelemetry.Should().BeFalse();
        options.OutputDirectory.Should().Be(Path.GetFullPath("fixtures"));
    }

    [Fact]
    public void Parse_EveryOption_IsApplied()
    {
        var (options, error) = LoadTargetOptions.Parse([
            "--out", "f", "--port", "6000", "--model", "slow", "--failure-rate", "0.25", "--callers", "405",
            "--tenants", "7", "--token-lifetime-minutes", "60", "--enable-tool-invocation", "--with-telemetry",
        ]);

        error.Should().BeNull();
        options!.Port.Should().Be(6000);
        options.Model.Name.Should().Be("slow");
        options.Model.TransientFailureRate.Should().Be(0.25);
        options.Callers.Should().Be(405);
        options.Tenants.Should().Be(7);
        options.TokenLifetime.Should().Be(TimeSpan.FromMinutes(60));
        options.EnableToolInvocation.Should().BeTrue();
        options.WithTelemetry.Should().BeTrue();
    }

    [Theory]
    [InlineData("--caller", "405")]
    [InlineData("--prot", "6000")]
    [InlineData("stray", "value")]
    public void Parse_AnOptionItDoesNotKnow_IsRejected(string name, string value)
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "f", name, value]);

        options.Should().BeNull();
        error.Should().Contain(name);
    }

    [Fact]
    public void Parse_AValueThatIsAnotherOption_IsRejected()
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "--with-telemetry"]);

        options.Should().BeNull("'--with-telemetry' is a switch, not a folder name");
        error.Should().Contain("--out needs a value");
    }

    [Fact]
    public void Parse_AnOptionWithNoValueAtTheEnd_IsRejected()
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "f", "--callers"]);

        options.Should().BeNull();
        error.Should().Contain("--callers needs a value");
    }

    [Fact]
    public void Parse_WithoutAnOutputFolder_IsRejected()
    {
        var (options, error) = LoadTargetOptions.Parse(["--port", "6000"]);

        options.Should().BeNull();
        error.Should().Contain("--out is required");
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--callers", "0")]
    [InlineData("--callers", "many")]
    [InlineData("--tenants", "1001")]
    [InlineData("--token-lifetime-minutes", "4")]
    public void Parse_AWholeNumberOutsideItsRange_IsRejected(string name, string value)
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "f", name, value]);

        options.Should().BeNull();
        error.Should().StartWith(name);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("half")]
    public void Parse_AFailureRateOutsideZeroToOne_IsRejected(string rate)
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "f", "--failure-rate", rate]);

        options.Should().BeNull("NaN parses as a number but is not a rate");
        error.Should().Contain("--failure-rate");
    }

    [Fact]
    public void Parse_AnUnknownModel_IsRejected()
    {
        var (options, error) = LoadTargetOptions.Parse(["--out", "f", "--model", "turbo"]);

        options.Should().BeNull();
        error.Should().Contain("turbo");
    }
}
