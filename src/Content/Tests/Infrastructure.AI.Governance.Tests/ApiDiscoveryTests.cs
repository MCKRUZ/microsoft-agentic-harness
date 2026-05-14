using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Infrastructure.AI.Governance.Tests;

public sealed class ApiDiscoveryTests
{
    private readonly ITestOutputHelper _output;
    public ApiDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpAgtPublicTypes()
    {
        var asm = Assembly.Load("AgentGovernance");
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.Namespace).ThenBy(t => t.Name))
            _output.WriteLine($"{t.Namespace}.{t.Name}");
    }

    [Fact]
    public void DumpGovernanceKernelApi()
    {
        var asm = Assembly.Load("AgentGovernance");
        var kernel = asm.GetExportedTypes().FirstOrDefault(t => t.Name == "GovernanceKernel");
        if (kernel is null) { _output.WriteLine("GovernanceKernel NOT FOUND"); return; }
        _output.WriteLine($"=== {kernel.FullName} ===");
        foreach (var p in kernel.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
        foreach (var m in kernel.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            _output.WriteLine($"  method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        foreach (var c in kernel.GetConstructors())
            _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }

    [Fact]
    public void DumpPolicyEngineApi()
    {
        var asm = Assembly.Load("AgentGovernance");
        foreach (var name in new[] { "PolicyEngine", "PolicyDecision", "PromptInjectionDetector", "DetectionResult", "AuditLogger", "AuditEmitter", "McpSecurityScanner" })
        {
            var type = asm.GetExportedTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) { _output.WriteLine($"{name}: NOT FOUND"); continue; }
            _output.WriteLine($"\n=== {type.FullName} ===");
            foreach (var c in type.GetConstructors())
                _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                _output.WriteLine($"  method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        }
    }

    [Fact]
    public void DumpPolicyRuleApi()
    {
        var asm = Assembly.Load("AgentGovernance");
        foreach (var name in new[] { "PolicyRule", "Policy", "ToolCallResult" })
        {
            var type = asm.GetExportedTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) { _output.WriteLine($"{name}: NOT FOUND"); continue; }
            _output.WriteLine($"\n=== {type.FullName} ===");
            foreach (var c in type.GetConstructors())
                _output.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                _output.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
        }
    }

    [Fact]
    public void DumpSupportingTypes()
    {
        var asm = Assembly.Load("AgentGovernance");
        foreach (var name in new[] { "GovernanceOptions", "DetectionConfig", "PolicyAction", "InjectionType", "ThreatLevel", "ConflictResolutionStrategy", "PolicyScope", "GovernanceEventType" })
        {
            var type = asm.GetExportedTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) { _output.WriteLine($"{name}: NOT FOUND"); continue; }
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
    }
}
