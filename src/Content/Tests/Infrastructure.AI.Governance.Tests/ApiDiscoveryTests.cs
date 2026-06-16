using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Infrastructure.AI.Governance.Tests;

/// <summary>
/// Pins the public API surface of the <c>AgentGovernance</c> package that this harness depends on.
/// Each fact asserts the expected types (and, where relevant, members) exist, so a removed or renamed
/// type in a future package upgrade fails the build instead of silently regressing. The reflected
/// signatures are still written to <see cref="ITestOutputHelper"/> for diagnostic/change-detection use.
/// </summary>
public sealed class ApiDiscoveryTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Initializes a new instance of the <see cref="ApiDiscoveryTests"/> class.</summary>
    /// <param name="output">xUnit sink used to dump reflected member signatures for diagnostics.</param>
    public ApiDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AgtAssembly_ExportsPublicTypes()
    {
        var asm = Assembly.Load("AgentGovernance");
        var types = asm.GetExportedTypes().OrderBy(t => t.Namespace).ThenBy(t => t.Name).ToArray();
        foreach (var t in types)
            _output.WriteLine($"{t.Namespace}.{t.Name}");

        Assert.NotEmpty(types);
    }

    [Fact]
    public void GovernanceKernel_TypeExists()
    {
        var asm = Assembly.Load("AgentGovernance");
        var kernel = asm.GetExportedTypes().FirstOrDefault(t => t.Name == "GovernanceKernel");

        Assert.NotNull(kernel);

        _output.WriteLine($"=== {kernel.FullName} ===");
        foreach (var p in kernel.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
        foreach (var m in kernel.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            _output.WriteLine($"  method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        foreach (var c in kernel.GetConstructors())
            _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }

    [Fact]
    public void PolicyEngineTypes_AllExist()
    {
        var asm = Assembly.Load("AgentGovernance");
        var expected = new[] { "PolicyEngine", "PolicyDecision", "PromptInjectionDetector", "DetectionResult", "AuditLogger", "AuditEmitter" };

        var missing = DumpAndCollectMissing(asm, expected);

        Assert.True(missing.Count == 0, $"AgentGovernance is missing expected types: {string.Join(", ", missing)}");
    }

    [Fact]
    public void PolicyRuleTypes_AllExist()
    {
        var asm = Assembly.Load("AgentGovernance");
        var expected = new[] { "PolicyRule", "Policy", "ToolCallResult" };

        var missing = DumpAndCollectMissing(asm, expected);

        Assert.True(missing.Count == 0, $"AgentGovernance is missing expected types: {string.Join(", ", missing)}");
    }

    [Fact]
    public void SupportingTypes_AllExist()
    {
        var asm = Assembly.Load("AgentGovernance");
        var expected = new[] { "GovernanceOptions", "DetectionConfig", "PolicyAction", "InjectionType", "ThreatLevel", "ConflictResolutionStrategy", "PolicyScope", "GovernanceEventType" };

        var missing = new List<string>();
        foreach (var name in expected)
        {
            var type = asm.GetExportedTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) { missing.Add(name); continue; }
            _output.WriteLine($"\n=== {type.FullName} (IsEnum={type.IsEnum}) ===");
            if (type.IsEnum)
            {
                foreach (var v in Enum.GetNames(type))
                    _output.WriteLine($"  {v} = {(int)Enum.Parse(type, v)}");
            }
            else
            {
                foreach (var c in type.GetConstructors())
                    _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
            }
        }

        Assert.True(missing.Count == 0, $"AgentGovernance is missing expected types: {string.Join(", ", missing)}");
    }

    private List<string> DumpAndCollectMissing(Assembly asm, IReadOnlyList<string> expected)
    {
        var missing = new List<string>();
        foreach (var name in expected)
        {
            var type = asm.GetExportedTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) { missing.Add(name); continue; }
            _output.WriteLine($"\n=== {type.FullName} ===");
            foreach (var c in type.GetConstructors())
                _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                _output.WriteLine($"  method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        }

        return missing;
    }
}
