using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Asserts that every governance and identity contract the harness registers in DI is actually
/// <strong>consumed</strong> by production code — not merely declared, implemented, and registered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect this exists to catch.</strong> The harness has now shipped a security control
/// that existed, looked correct, had thorough unit tests, and was never invoked, four separate
/// times: <c>ToolPermissionFilter</c>, <c>GoverningToolContextProvider</c>, both recall providers,
/// and <c>IAgentIdentityValidator</c> — whose <c>CanInvoke</c> was a complete fail-closed RBAC
/// implementation with fifteen passing tests and, until #311, no production caller at all. Three
/// doc comments meanwhile told readers it was enforcing.
/// </para>
/// <para>
/// <strong>Why unit tests cannot catch it.</strong> The control's own tests exercise the control
/// and pass. Nothing fails when the call that would reach it is simply never written. A test can
/// only fail for code that exists, so the check has to be over the source itself — the same
/// reasoning as <see cref="ToolCallAdmissionChokepointTests"/>, pointed the other way. That test
/// asserts nothing <em>but</em> the chain calls the gates; this one asserts something calls each
/// registered control at all.
/// </para>
/// <para>
/// <strong>What counts as a consumer.</strong> Any production file that names the interface and is
/// not its own declaration, not a type that implements it, and not a DI registration. That covers
/// constructor injection, <c>GetRequiredService</c>, and an ambient static — the three ways a
/// caller reaches a service, only the first of which reflection would see.
/// </para>
/// <para>
/// <strong>If this test fails,</strong> the answer is one of exactly two things, and "add it to the
/// exemptions" is neither by default: wire the control onto the path it was written to guard, or
/// delete it along with the documentation claiming it is enforcing. Leaving a control that only
/// appears to be live is the outcome this forbids.
/// </para>
/// </remarks>
public sealed class SecurityControlHasACallerTests
{
    /// <summary>
    /// Interface directories whose contents are access-control or governance decisions. A contract
    /// declared here is load-bearing by construction, so an uncalled one is a finding rather than
    /// dead weight.
    /// </summary>
    private static readonly string[] GuardedInterfaceFolders =
    [
        Path.Combine("Interfaces", "Governance"),
        Path.Combine("Interfaces", "Identity")
    ];

    /// <summary>
    /// Contracts that are deliberately not consumed by the harness itself, each with the reason.
    /// Anything added here needs an argument for why an unconsumed security contract is correct.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // A pure extension point: the harness ships no implementation and never calls one directly.
        // Its consumer is IToolCallObserverChain, which resolves IEnumerable<IToolCallObserver> —
        // so it does in fact have a caller, and this entry exists only to document that the
        // zero-implementations state is intended rather than an oversight.
        ["IToolCallObserver"] = "consumer extension point; consumed as a collection by IToolCallObserverChain"

        // IMcpSecurityScanner was carried here as a known defect — this guard's first run found it
        // registered twice and called nowhere, the fifth instance of the pattern. It is now consumed
        // by ScanningMcpToolProvider, which screens every tool definition an external MCP server
        // advertises before it can reach the model (#313), so the entry is gone rather than renewed.
    };

    [Fact]
    public void EveryRegisteredGovernanceContractHasAProductionConsumer()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var contracts = FindGuardedContracts(contentRoot);

        contracts.Should().NotBeEmpty(
            "a scan that discovered no contracts would pass vacuously — the folders it reads must exist");

        var productionFiles = Directory
            .EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f, contentRoot))
            .Select(f => (Path: f, Code: StripCommentsAndStrings(File.ReadAllText(f))))
            .ToArray();

        var uncalled = new List<string>();

        foreach (var (contract, declarationPath) in contracts)
        {
            if (Exempt.ContainsKey(contract))
                continue;

            var consumers = productionFiles
                .Where(f => !string.Equals(f.Path, declarationPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(f.Code, $@"\b{contract}\b"))
                .Where(f => !IsRegistrationOnly(f.Code, contract))
                .Where(f => !Implements(f.Code, contract))
                .Select(f => Path.GetRelativePath(contentRoot, f.Path))
                .ToArray();

            if (consumers.Length == 0)
                uncalled.Add(contract);
        }

        uncalled.Should().BeEmpty(
            "a governance contract that is declared, implemented and registered but never consumed is "
            + "a security control that does not run, while its documentation says it does. That has "
            + "shipped four times. Wire each of these onto the path it guards, or delete it together "
            + "with the docs claiming it is enforced. Uncalled: " + string.Join(", ", uncalled));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        // An empty offender list is the same shape whether nothing violates the rule or nothing is
        // being read. These prove each classifier does its job, so a pass means the scan ran.
        Implements("public sealed class X : IAgentToolAuthorizationGate { }", "IAgentToolAuthorizationGate")
            .Should().BeTrue("an implementation is not a consumer");
        Implements("public sealed class X : Base, IAgentToolAuthorizationGate { }", "IAgentToolAuthorizationGate")
            .Should().BeTrue("an implementation is not a consumer when the interface is not first either");

        IsRegistrationOnly(
            "services.AddScoped<IAgentToolAuthorizationGate, DefaultAgentToolAuthorizationGate>();",
            "IAgentToolAuthorizationGate")
            .Should().BeTrue("registering a service is not calling it — that is the whole defect");

        // The shape this repo actually writes. The first version of this control asserted only the
        // unqualified form above, so it passed while the classifier matched none of the eight
        // namespace-qualified registrations in Application.AI.Common — the guard was blind and its
        // own mutation test said otherwise. A control that does not exercise the real input is not
        // a control.
        IsRegistrationOnly(
            "services.AddScoped<Interfaces.Governance.IAgentToolAuthorizationGate, "
            + "Services.Governance.DefaultAgentToolAuthorizationGate>();",
            "IAgentToolAuthorizationGate")
            .Should().BeTrue("registrations here are namespace-qualified, and are still registrations");

        Implements("private readonly IAgentToolAuthorizationGate _gate;", "IAgentToolAuthorizationGate")
            .Should().BeFalse("a field of the interface type is exactly what a consumer looks like");
        IsRegistrationOnly("private readonly IAgentToolAuthorizationGate _gate;", "IAgentToolAuthorizationGate")
            .Should().BeFalse();
    }

    [Fact]
    public void TheScanReadsARepresentativeNumberOfFiles()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        Directory.EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Count(f => !IsExcluded(f, contentRoot))
            .Should().BeGreaterThan(500);
    }

    /// <summary>
    /// Finds every public interface declared under a guarded folder, returning its name and the file
    /// that declares it.
    /// </summary>
    private static IReadOnlyList<(string Contract, string DeclarationPath)> FindGuardedContracts(string contentRoot)
    {
        var found = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, "I*.cs", SearchOption.AllDirectories))
        {
            if (IsExcluded(file, contentRoot))
                continue;

            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (!GuardedInterfaceFolders.Any(folder => directory.Contains(folder, StringComparison.OrdinalIgnoreCase)))
                continue;

            var code = StripCommentsAndStrings(File.ReadAllText(file));
            foreach (Match match in Regex.Matches(code, @"\bpublic\s+interface\s+(I\w+)"))
                found.Add((match.Groups[1].Value, file));
        }

        return found;
    }

    /// <summary>
    /// Whether the only mention of the contract in this file is a DI registration. Registering a
    /// service is precisely what every one of the four dead controls did have.
    /// </summary>
    /// <remarks>
    /// The optional <c>(?:[\w.]+\.)?</c> qualifier is load-bearing, not defensive. Every governance
    /// contract in <c>Application.AI.Common/DependencyInjection.cs</c> is registered namespace-
    /// qualified — <c>AddScoped&lt;Interfaces.Governance.IToolInvocationGovernor, …&gt;</c> — so a
    /// pattern anchored directly on the bare interface name matched none of them. That made the DI
    /// file look like a <em>consumer</em> of all eight, and the guard structurally unable to report
    /// any of them: exactly the blind spot it exists to remove.
    /// </remarks>
    private static bool IsRegistrationOnly(string code, string contract)
    {
        var mentions = Regex.Matches(code, $@"\b{contract}\b").Count;
        var registrations = Regex.Matches(
            code, $@"Add(?:Scoped|Singleton|Transient|Keyed\w+)\s*<\s*(?:[\w.]+\.)?{contract}\b").Count;
        return mentions > 0 && mentions == registrations;
    }

    /// <summary>
    /// Whether this file declares a type implementing the contract. An implementation names the
    /// interface without being a caller of it.
    /// </summary>
    private static bool Implements(string code, string contract) =>
        Regex.IsMatch(code, $@"\b(?:class|record|struct)\s+\w+(?:<[^>]*>)?\s*:\s*[^{{;]*\b{contract}\b");

    private static bool IsExcluded(string path, string contentRoot)
    {
        var relative = Path.GetRelativePath(contentRoot, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("Tests", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes comments and string literals so only compiled code is matched — a doc comment naming
    /// a contract must not count as calling it, which is exactly how the dead controls read as live.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//[^\n]*", " ");
        return Regex.Replace(withoutLineComments, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
    }
}
