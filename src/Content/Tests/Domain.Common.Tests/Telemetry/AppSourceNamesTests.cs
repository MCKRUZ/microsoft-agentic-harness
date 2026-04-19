using Domain.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="AppSourceNames"/> constants and <see cref="AppInstrument"/> singletons.
/// </summary>
public class AppSourceNamesTests
{
    [Fact]
    public void AgenticHarness_HasExpectedValue()
    {
        AppSourceNames.AgenticHarness.Should().Be("AgenticHarness");
    }

    [Fact]
    public void AgenticHarnessMediatR_HasExpectedValue()
    {
        AppSourceNames.AgenticHarnessMediatR.Should().Be("AgenticHarness.MediatR");
    }

    [Fact]
    public void AppInstrument_Source_IsNotNull()
    {
        AppInstrument.Source.Should().NotBeNull();
        AppInstrument.Source.Name.Should().Be(AppSourceNames.AgenticHarness);
    }

    [Fact]
    public void AppInstrument_Meter_IsNotNull()
    {
        AppInstrument.Meter.Should().NotBeNull();
        AppInstrument.Meter.Name.Should().Be(AppSourceNames.AgenticHarness);
    }

    [Fact]
    public void AppInstrument_Source_IsSingleton()
    {
        var source1 = AppInstrument.Source;
        var source2 = AppInstrument.Source;

        source1.Should().BeSameAs(source2);
    }
}
