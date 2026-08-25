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
/// <strong>What this file does and does not cover.</strong> The consumer scan reads
/// <em>interfaces</em> declared under the guarded folders, so of the four defects named above it could
/// only ever have caught the last. The other three are concrete classes implementing no guarded
/// contract, and no exemption list can be curated into covering them without becoming the very smell
/// this test warns about. <see cref="NoFactoryDecidesAPerRunFactAtConstructionTime"/> covers the
/// second failure mode those three shared — a control wired correctly but decided at the wrong moment
/// — by reading source shape rather than contracts.
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

    /// <summary>
    /// Reads that mean a factory is answering a question whose answer belongs to one run, not to the
    /// lifetime of a cached object. See <see cref="NoFactoryDecidesAPerRunFactAtConstructionTime"/>.
    /// </summary>
    private static readonly (string Pattern, string What)[] PerRunFactPatterns =
    [
        (@"\bEnforceToolInvocation\b", "the global tool-invocation enforcement switch"),
        (@"\b\w*Accessor\s*\.\s*Current\b", "an ambient per-run accessor")
    ];

    [Fact]
    public void EveryRegisteredGovernanceContractHasAProductionConsumer()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var contracts = FindGuardedContracts(contentRoot);

        contracts.Should().NotBeEmpty(
            "a scan that discovered no contracts would pass vacuously — the folders it reads must exist");

        var productionFiles = Directory
            .EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .Select(f => (Path: f, Code: SourceScan.StripCommentsAndStrings(File.ReadAllText(f))))
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
            .Count(f => !SourceScan.IsExcluded(f, contentRoot))
            .Should().BeGreaterThan(500);
    }

    /// <summary>
    /// Asserts that no factory answers a question whose answer is only true for the duration of one
    /// run — the second way a security control ends up not running, and the one the consumer scan
    /// above is structurally blind to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The defect this exists to catch (#347).</strong> A factory builds an agent once and the
    /// agent is cached. Enforcement, though, can be armed per run: a bundle executes an
    /// externally-authored agent under a per-caller capability envelope published ambiently for that
    /// run only. <c>GoverningToolContextProvider</c> was attached behind the host's global enforcement
    /// switch, read at construction — a moment that cannot see the envelope — so on the default
    /// composition a bundle's progressive-disclosure tools reached the model ungoverned. The control
    /// was registered, wired and consumed; it simply answered the question at the wrong moment.
    /// </para>
    /// <para>
    /// The rule the harness already follows everywhere else is stated on
    /// <c>ToolAdmissionAccessor</c>: wire unconditionally, decide at invocation.
    /// <c>ToolChainBuilder</c> — the other channel tools reach the model by — has always wrapped for
    /// governance with no config read anywhere near it. This makes that rule structural for factories
    /// rather than a comment at each site.
    /// </para>
    /// <para>
    /// <strong>If this fails,</strong> the fix is not an exemption: move the decision to the moment it
    /// can be answered. A gate belongs at invocation, where the ambient state it depends on is live.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoFactoryDecidesAPerRunFactAtConstructionTime()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        var factoryFiles = Directory
            .EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .Where(f => (Path.GetDirectoryName(f) ?? string.Empty)
                .Contains(Path.DirectorySeparatorChar + "Factories", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Control: a scan that found no factories would pass while reading nothing.
        factoryFiles.Should().NotBeEmpty("the folders this reads must exist for its verdict to mean anything");

        // Control: each pattern must fire on the shape it is written for. Without this the assertion
        // below is satisfied equally by a rule that matches nothing — the blind-guard failure this
        // file's other classifiers already had once.
        Regex.IsMatch("if (config.AI?.Governance?.EnforceToolInvocation == true)", PerRunFactPatterns[0].Pattern)
            .Should().BeTrue("control: the enforcement-switch pattern must match the read it forbids");
        Regex.IsMatch("var envelope = CapabilityEnvelopeAccessor.Current;", PerRunFactPatterns[1].Pattern)
            .Should().BeTrue("control: the ambient-accessor pattern must match the read it forbids");

        var offenders = new List<string>();

        foreach (var file in factoryFiles)
        {
            // Comments are stripped first on purpose: this file's own explanation of why the condition
            // was removed names the switch, and a guard that flagged its own rationale would be useless.
            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));

            foreach (var (pattern, what) in PerRunFactPatterns)
            {
                if (Regex.IsMatch(code, pattern))
                    offenders.Add($"{Path.GetRelativePath(contentRoot, file)} reads {what}");
            }
        }

        offenders.Should().BeEmpty(
            "a factory runs once and its output is cached, so anything it decides is frozen before the "
            + "run that needs it exists. Reading per-run state there produces a control that looks wired "
            + "and does not fire — #347, where a bundle's disclosure tools reached the model ungoverned. "
            + "Attach unconditionally and gate at invocation instead. Found: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Every <c>*ConfigValidator</c> must be bound into the options pipeline, because a validator that
    /// nothing binds runs nowhere and enforces nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The defect this exists to catch, in the shape it actually shipped.</strong>
    /// <c>ToolCallReplayConfigValidator</c> was written, fully unit-tested, and documented in three
    /// separate XML comments as the startup enforcement for two bounds — one of them the
    /// confidentiality ceiling above which structural secret redaction stops being trustworthy. It was
    /// never added to <c>RegisterValidatedConfigSections</c>, so none of its rules ever ran in any
    /// host. Its own doc comment said "auto-discovered via <c>AddValidatorsFromAssembly</c> — no manual
    /// registration required", which is true of the DI registration and irrelevant to whether anything
    /// resolves it: nothing validates a config POCO unless an <c>AddOptions</c> chain asks it to.
    /// </para>
    /// <para>
    /// <strong>Why the existing guards missed it.</strong> The validator's own tests pass — they
    /// construct it directly. <c>ValidateOnBuildSweepTests</c> passes — an unregistered validator
    /// breaks no service graph. The interface scan above passes — a validator implements no guarded
    /// contract. Every signal available said the control was fine, because each one measured something
    /// other than "does this ever run in a host".
    /// </para>
    /// <para>
    /// Written over every validator rather than the one that broke, so the next config class cannot
    /// land unregistered either — the specific fix would have left the mechanism just as forgettable.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryConfigValidator_IsBoundIntoTheOptionsPipeline()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        var validatorNames = Directory
            .EnumerateFiles(contentRoot, "*ConfigValidator.cs", SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Control: a scan that found no validators would pass while reading nothing — the blind-guard
        // failure this file's own remarks warn about.
        validatorNames.Should().NotBeEmpty("the validators this reads must exist for its verdict to mean anything");

        // The composition root is the only place an options binding can live, so its text is the
        // authority on what actually runs. Read as source rather than resolved through DI because an
        // unbound validator is still perfectly resolvable — that is precisely what made it invisible.
        var compositionRoot = File.ReadAllText(Path.Combine(
            contentRoot, "Presentation", "Presentation.Common", "Extensions", "IServiceCollectionExtensions.cs"));
        var wiring = SourceScan.StripCommentsAndStrings(compositionRoot);

        // Control: the needle must match a binding known to be present, or "no offenders" would be
        // satisfied by a scan that cannot see any binding at all.
        wiring.Should().Contain("GovernanceConfigValidator",
            "control: a known-bound validator must be visible to this scan");

        var unbound = validatorNames
            .Where(name => !wiring.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var unexpected = unbound.Except(KnownUnboundConfigValidators, StringComparer.Ordinal).ToArray();

        unexpected.Should().BeEmpty(
            "a config validator that no AddOptions chain binds never runs, however thoroughly it is "
            + "tested and however confidently its doc comment says otherwise — AddValidatorsFromAssembly "
            + "registers it for resolution, it does not cause anything to resolve it. Either bind it in "
            + "RegisterValidatedConfigSections or delete it with the documentation claiming it enforces. "
            + "Unbound: " + string.Join(", ", unexpected));

        // The ratchet half: the allowlist may only ever shrink. Without this, working an entry off the
        // list leaves a stale name that would silently re-admit a regression under the same type name.
        KnownUnboundConfigValidators
            .Except(unbound, StringComparer.Ordinal)
            .Should().BeEmpty(
                "these validators are now bound — remove them from KnownUnboundConfigValidators so the "
                + "list keeps meaning 'known debt' rather than 'permanently excused'");
    }

    /// <summary>
    /// Config validators that exist, are tested, and are bound nowhere — pre-existing debt this guard
    /// found rather than caused. Tracked so the list can only shrink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are <strong>not</strong> exemptions in the sense this file's remarks forbid. Each is a
    /// real instance of the same defect the test above describes, several with doc comments asserting
    /// they refuse to boot on invalid config — which they do not, because nothing binds them. They are
    /// listed rather than fixed here because binding a validator changes host behaviour: a deployment
    /// whose configuration is already invalid stops booting. That is the correct outcome and it is a
    /// per-subsystem change with its own verification, not something to bundle into an unrelated PR.
    /// </para>
    /// <para>
    /// The entry that motivated this guard — <c>ToolCallReplayConfigValidator</c> — is deliberately
    /// absent: it was bound in the same change, which is why the list is seven names and not eight.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownUnboundConfigValidators =
    [
        "AutonomyConfigValidator",
        "ConditionalBranchConfigValidator",
        "HumanGateConfigValidator",
        "LlmCallConfigValidator",
        "SubPlanConfigValidator",
        "ToolAuthorizationConfigValidator",
        "ToolUseConfigValidator",
    ];

    /// <summary>
    /// Finds every public interface declared under a guarded folder, returning its name and the file
    /// that declares it.
    /// </summary>
    private static IReadOnlyList<(string Contract, string DeclarationPath)> FindGuardedContracts(string contentRoot)
    {
        var found = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, "I*.cs", SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(file, contentRoot))
                continue;

            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (!GuardedInterfaceFolders.Any(folder => directory.Contains(folder, StringComparison.OrdinalIgnoreCase)))
                continue;

            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));
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

}
