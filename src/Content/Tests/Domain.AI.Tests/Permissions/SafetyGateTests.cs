using Domain.AI.Permissions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Permissions;

/// <summary>
/// Tests for <see cref="SafetyGate"/> record — construction, bypass immunity.
/// </summary>
public sealed class SafetyGateTests
{
    [Fact]
    public void Constructor_SetsPathPatternAndDescription()
    {
        var gate = new SafetyGate(".git/", "Git internals are always protected");

        gate.PathPattern.Should().Be(".git/");
        gate.Description.Should().Be("Git internals are always protected");
    }

    [Fact]
    public void IsBypassImmune_AlwaysTrue()
    {
        var gate = new SafetyGate(".ssh/", "SSH keys are protected");

        gate.IsBypassImmune.Should().BeTrue();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var g1 = new SafetyGate(".env", "Environment secrets");
        var g2 = new SafetyGate(".env", "Environment secrets");

        g1.Should().Be(g2);
    }

    [Fact]
    public void Equality_DifferentPattern_AreNotEqual()
    {
        var g1 = new SafetyGate(".git/", "Git");
        var g2 = new SafetyGate(".ssh/", "SSH");

        g1.Should().NotBe(g2);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new SafetyGate(".git/", "Git");
        var updated = original with { Description = "Updated description" };

        updated.Description.Should().Be("Updated description");
        original.Description.Should().Be("Git");
    }
}
