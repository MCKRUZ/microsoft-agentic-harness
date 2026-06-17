using System.Linq;
using System.Reflection;
using Domain.AI.A2A;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// PR-7 canary tests. Two purposes:
/// </summary>
/// <list type="number">
/// <item><description>Pin the harness <see cref="A2AEnvelope"/> schema version
/// so any breaking shape change forces an explicit bump.</description></item>
/// <item><description>Assert <c>Microsoft.Agents.AI</c> (MAF) 1.10.0 still
/// lacks a public A2A surface. The moment MAF ships one, this test fails and
/// the harness should switch to wrapping it instead of carrying its own
/// transport implementation.</description></item>
/// </list>
public sealed class A2AVersionPinTests
{
    [Fact]
    public void Envelope_schema_version_is_pinned_to_1()
    {
        A2AEnvelope.CurrentSchemaVersion
            .Should().Be(1,
                "PR-7 ships envelope schema v1; bumping requires updating the version-pin test and writing a migration note in documentation/architecture/a2a-message-contract.md");
    }

    [Fact]
    public void Maf_does_not_yet_expose_a_public_A2A_surface()
    {
        // Force MAF assembly load — without a hard reference the test domain
        // may not have it loaded yet and the canary would be vacuously true.
        var mafProbe = typeof(Microsoft.Agents.AI.AIAgent).Assembly;
        mafProbe.Should().NotBeNull();

        // Canary: when MAF adds an A2A namespace / interface, this test fires
        // and the harness should switch to wrapping the MAF surface. The
        // probe is a heuristic — any public type under Microsoft.Agents.AI*
        // whose name contains "A2A" or "AgentToAgent" trips it.
        var mafAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal) == true)
            .ToList();

        mafAssemblies.Should().NotBeEmpty("the MAF assemblies must be loaded for the canary to be meaningful");

        var probes = mafAssemblies
            .SelectMany(a =>
            {
                try { return a.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
            })
            .Where(t => t is not null)
            .Where(t =>
                (t!.FullName?.Contains("A2A", StringComparison.OrdinalIgnoreCase) == true) ||
                (t.FullName?.Contains("AgentToAgent", StringComparison.OrdinalIgnoreCase) == true))
            .Select(t => t!.FullName)
            .ToList();

        probes.Should().BeEmpty(
            "Microsoft Agent Framework 1.10.0 does not expose A2A primitives; if this test fails MAF has added them and the harness should wrap them — see documentation/architecture/a2a-message-contract.md 'Version pin' section");
    }

    [Fact]
    public void Maf_assembly_is_pinned_to_1_10_x()
    {
        var maf = typeof(Microsoft.Agents.AI.AIAgent).Assembly;
        var version = maf.GetName().Version;
        version.Should().NotBeNull();
        version!.Major.Should().Be(1, "harness pins to MAF 1.x");
        version.Minor.Should().Be(10,
            "harness pins to MAF 1.10.x (bumped from 1.9.x to adopt Azure AI Foundry Responses agents via " +
            "Microsoft.Agents.AI.Foundry); bumping minor requires re-running canary");
    }
}
