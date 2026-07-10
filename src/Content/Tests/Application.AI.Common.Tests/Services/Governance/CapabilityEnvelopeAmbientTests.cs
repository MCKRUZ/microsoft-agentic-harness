using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Governance;

/// <summary>
/// Tests the per-run ambient primitive the capability envelope rides on: the
/// <see cref="CapabilityEnvelopeAccessor"/> that publishes the active grant. It must be absent outside a run
/// and must restore the previous value on dispose so nested runs cannot leak state. (The envelope's presence
/// is itself the signal that forces governance on for a bundle flow — there is no separate enforcement flag.)
/// </summary>
public sealed class CapabilityEnvelopeAmbientTests
{
    [Fact]
    public void Envelope_IsNull_OutsideAnyRun()
        => CapabilityEnvelopeAccessor.Current.Should().BeNull();

    [Fact]
    public void Envelope_Begin_PublishesThenRestoresOnDispose()
    {
        var envelope = new CapabilityEnvelope { AllowedTools = ["t"], AutonomyCeiling = AutonomyLevel.Supervised };

        using (CapabilityEnvelopeAccessor.Begin(envelope))
            CapabilityEnvelopeAccessor.Current.Should().BeSameAs(envelope);

        CapabilityEnvelopeAccessor.Current.Should().BeNull("the scope restored the previous (absent) value");
    }

    [Fact]
    public void Envelope_NestedBegin_RestoresOuter_NotJustNull()
    {
        var outer = new CapabilityEnvelope { AllowedTools = ["outer"] };
        var inner = new CapabilityEnvelope { AllowedTools = ["inner"] };

        using (CapabilityEnvelopeAccessor.Begin(outer))
        {
            using (CapabilityEnvelopeAccessor.Begin(inner))
                CapabilityEnvelopeAccessor.Current.Should().BeSameAs(inner);

            CapabilityEnvelopeAccessor.Current.Should().BeSameAs(outer, "the inner scope restored the outer envelope");
        }
    }

    [Fact]
    public void Envelope_Begin_NullEnvelope_Throws()
    {
        var act = () => CapabilityEnvelopeAccessor.Begin(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
