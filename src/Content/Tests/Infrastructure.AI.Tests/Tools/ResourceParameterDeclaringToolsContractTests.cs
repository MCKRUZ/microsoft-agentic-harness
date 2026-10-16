using System.Reflection;
using Application.AI.Common.Interfaces.Tools;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// A ratchet on which tools declare <c>ITool.ResourceParametersByOperation</c> (#825): adding a declaring tool
/// fails here until someone adds its missing-parameter contract test.
/// </summary>
/// <remarks>
/// <para>
/// The contract: <c>ResourceParameterExtractor</c> reads an omitted or null declared path/host as "the call did
/// not ask for one" and returns an empty request that scoping passes. That is safe only while every declaring
/// tool fails when the parameter is missing instead of defaulting somewhere. <see cref="FileSystemToolTests"/>
/// pins it for the file tool; this test makes sure the next declaring tool cannot slip in without the same pin.
/// </para>
/// <para>
/// <b>What it covers.</b> Concrete <see cref="ITool"/> types in the assemblies this test project references
/// (<c>Infrastructure.*</c> and <c>Application.*</c>), found by reflection, not by text search. It is not an
/// enumeration of every assembly a consumer might add: a tool in an assembly this project does not reference is
/// invisible to it, and that tool's author owns the same obligation.
/// </para>
/// </remarks>
public sealed class ResourceParameterDeclaringToolsContractTests
{
    private static readonly string[] AssemblyPrefixes = ["Infrastructure.", "Application."];

    [Fact]
    public void OnlyToolsWithAMissingParameterContractTestDeclareResourceParameters()
    {
        var declaring = ConcreteToolTypes()
            .Where(DeclaresResourceParameters)
            .Select(t => t.Name)
            .Order()
            .ToArray();

        declaring.Should().BeEquivalentTo(
            [nameof(EvidenceReaderTool), nameof(FileSystemTool)],
            "a tool that declares ITool.ResourceParametersByOperation must fail when a declared path/host " +
            "parameter is missing, null, blank or the wrong type, because the extractor treats an omitted value " +
            "as 'not requested' and path/host scoping passes it. Add a test like " +
            "FileSystemToolTests.ExecuteAsync_DeclaredResourceParameterMissingOrUnusable_FailsAndTouchesNothing " +
            "(or EvidenceReaderToolTests' equivalent) for the new tool, then add it to this expected set.");
    }

    [Fact]
    public void TheScanActuallyFindsTools()
    {
        // Control: the scan above passes vacuously if reflection sees no tools at all.
        ConcreteToolTypes().Should().Contain(t => t == typeof(FileSystemTool));
    }

    private static IEnumerable<Type> ConcreteToolTypes() =>
        CandidateAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t));

    private static bool DeclaresResourceParameters(Type type) =>
        type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(p => p.Name.EndsWith(nameof(ITool.ResourceParametersByOperation), StringComparison.Ordinal));

    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var roots = new[] { typeof(FileSystemTool).Assembly, typeof(ResourceParameterDeclaringToolsContractTests).Assembly };

        return roots
            .SelectMany(a => a.GetReferencedAssemblies().Prepend(a.GetName()))
            .Where(n => n.Name is not null && AssemblyPrefixes.Any(p => n.Name.StartsWith(p, StringComparison.Ordinal)))
            .Select(n => n.Name!)
            .Distinct()
            .Select(Assembly.Load)
            .ToArray();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
