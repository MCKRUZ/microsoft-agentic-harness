using Application.Core.Validation;
using Domain.Common.Config.AI.DirectToolInvocation;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="DirectToolInvocationConfigValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// These are startup guards, and two of them exist because the failure they prevent is indistinguishable
/// from a broken host. A non-positive output ceiling slices the tool's output with a negative length, so a
/// tool call that worked perfectly surfaces to the caller as a <c>500</c>; a non-positive deadline cancels
/// every invocation before the tool starts, so the surface answers <c>504</c> to everything. In both cases
/// the response says nothing about configuration, and the operator's reasonable conclusion is that the
/// feature does not work.
/// </para>
/// <para>
/// The defaults-are-valid test is the one that keeps the rest honest: every rule here is unconditional, so
/// if the shipped defaults did not satisfy them, every host in the solution would fail to start.
/// </para>
/// </remarks>
public sealed class DirectToolInvocationConfigValidatorTests
{
    private readonly DirectToolInvocationConfigValidator _sut = new();

    [Fact]
    public void The_shipped_defaults_are_valid()
    {
        // Every rule is unconditional, so a default that violated one would stop every host booting —
        // including the hosts that never enable this surface.
        _sut.Validate(new DirectToolInvocationConfig()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_defaults_are_valid_even_though_the_surface_ships_disabled()
    {
        // The rules deliberately do not key off Enabled. A bad cap is a misconfiguration whether or not
        // the feature is switched on, and finding it at startup beats finding it the day it is enabled.
        var config = new DirectToolInvocationConfig { Enabled = false };

        _sut.Validate(config).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_output_ceiling_is_refused(int ceiling)
    {
        // Left unchecked this reaches output.AsSpan(0, negative) and throws, so a successful tool call
        // is reported to the caller as a fault of the host.
        var config = new DirectToolInvocationConfig { MaxOutputCharacters = ceiling };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_deadline_is_refused(int seconds)
    {
        // A zero deadline cancels every invocation before the tool starts. The surface then answers 504
        // to everything, which reads as an outage rather than a mistyped limit.
        var config = new DirectToolInvocationConfig { InvocationTimeout = TimeSpan.FromSeconds(seconds) };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_request_size_cap_is_refused(int bytes)
    {
        var config = new DirectToolInvocationConfig { MaxRequestBytes = bytes };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_parameter_cap_is_refused(int count)
    {
        // Zero would refuse every invocation that passes an argument, which is nearly all of them.
        var config = new DirectToolInvocationConfig { MaxParameterCount = count };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_output_ceiling_near_int_max_is_refused()
    {
        // The realistic way to land here is an operator writing 2147483647 to mean "no limit" — a value
        // this large is meaningless for this surface, so the validator names the setting at startup
        // rather than let the host boot with it.
        var config = new DirectToolInvocationConfig { MaxOutputCharacters = int.MaxValue };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_output_ceiling_at_the_documented_maximum_is_accepted()
    {
        // The bound must admit the value it advertises, or the constant is a lie the operator finds
        // out about at startup.
        var config = new DirectToolInvocationConfig
        {
            MaxOutputCharacters = DirectToolInvocationConfigValidator.MaxOutputCharactersCeiling
        };

        _sut.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_deadline_beyond_what_CancelAfter_accepts_is_refused()
    {
        // CancellationTokenSource.CancelAfter refuses a delay past int.MaxValue milliseconds (~24.85
        // days) and throws inside the invocation, where the generic catch turns it into a 500 — for
        // every call, with nothing naming the setting. Same shape as the output-ceiling overflow one
        // rule up, guarded for the same reason.
        var config = new DirectToolInvocationConfig { InvocationTimeout = TimeSpan.FromDays(30) };

        _sut.Validate(config).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_deadline_at_the_documented_maximum_is_accepted()
    {
        var config = new DirectToolInvocationConfig
        {
            InvocationTimeout = DirectToolInvocationConfigValidator.MaxInvocationTimeout
        };

        _sut.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_failure_names_the_setting_that_is_wrong()
    {
        // The whole point of failing at startup rather than at request time: the operator is told which
        // key to fix, instead of watching a surface refuse everything for no stated reason.
        var config = new DirectToolInvocationConfig { MaxOutputCharacters = 0 };

        var result = _sut.Validate(config);

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain(nameof(DirectToolInvocationConfig.MaxOutputCharacters));
    }
}
