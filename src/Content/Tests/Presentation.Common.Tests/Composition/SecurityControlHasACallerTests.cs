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
    /// <para>
    /// <strong>Scope: configuration validators only, decided by what a validator validates.</strong>
    /// A validator needs an <c>AddOptions</c> chain only when the type it validates is a configuration
    /// section bound from <c>appsettings</c> — every one of those lives under
    /// <c>Domain.Common.Config</c>. A validator over a <em>runtime payload</em> is invoked a different
    /// way: a consumer resolves <c>IValidator&lt;T&gt;</c> for the concrete type, so the validator's own
    /// name appears in no wiring file while it runs on every call. The five planner step-config
    /// validators are exactly that shape — <c>PlanValidator.ValidateStepConfigurations</c> resolves and
    /// applies each one against every step of every plan, and
    /// <c>PlanValidatorTests</c>'s real-container tests prove it. Scoping by the validated type's
    /// namespace separates the two mechanisms cleanly, with no exceptions in either direction.
    /// </para>
    /// <para>
    /// <strong>This is the third false alarm this guard has produced, and the reason it is scoped
    /// rather than allowlisted.</strong> Matching on filename swept in two live <c>IHostedService</c>
    /// validators; reading a single wiring file reported anything registered in a subsystem partial as
    /// unbound; and treating an options binding as the only invocation mechanism reported the five
    /// planner validators as dead debt (#514) when a prior audit had already proved they run. Each
    /// fix narrowed the question the guard asks toward the one it actually means: <em>is there
    /// something that causes this validator to run?</em> An allowlist would have recorded the wrong
    /// answer permanently instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryConfigValidator_IsBoundIntoTheOptionsPipeline()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        // Only FluentValidation validators. A name ending in ConfigValidator is not enough: the repo
        // also validates config from IHostedService implementations, which self-register through
        // AddHostedService in their own subsystem and need no options binding at all. Matching on the
        // name alone reported two live controls (ToolAuthorizationConfigValidator,
        // AutonomyConfigValidator) as unbound debt — a guard against inert machinery that was itself
        // producing false alarms, which is the fastest way to make one get ignored.
        var consumerResolvedTypes = ConsumerResolvedValidatedTypes
            .Select(e => e.ValidatedType)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = Directory
            .EnumerateFiles(contentRoot, "*ConfigValidator.cs", SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            // Candidacy stays on the BROAD predicate. Narrowing it to "files whose type argument I
            // could parse" would silently exempt every validator whose base names a qualified or
            // generic argument — dropping it from the scan entirely rather than reporting it. That
            // is the one direction this guard must never fail in, and it is not hypothetical:
            // EscalationConfig is declared in two namespaces, so the natural disambiguation
            // `AbstractValidator<Governance.EscalationConfig>` would make EscalationConfigValidator
            // vanish and let #516's defect land again unseen.
            .Where(f => Regex.IsMatch(
                SourceScan.StripCommentsAndStrings(File.ReadAllText(f)),
                @":\s*AbstractValidator\s*<"))
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Validated: ValidatedTypeName(f)))
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .ToArray();

        // Control: a scan that found no validators would pass while reading nothing — the blind-guard
        // failure this file's own remarks warn about.
        candidates.Should().NotBeEmpty("the validators this reads must exist for its verdict to mean anything");

        // Control: the AbstractValidator filter must actually exclude the other shape, or it is doing
        // nothing and the false alarms come straight back.
        candidates.Should().NotContain(c => c.Name == "ToolAuthorizationConfigValidator",
            "control: an IHostedService validator must not be treated as needing an options binding");

        // Control: the exemption set must be non-empty and cover a type known to be consumer-run,
        // or the guard passes only because nothing is excluded — indistinguishable from working
        // right up until it reported five false alarms again. That each entry is STILL resolved is
        // checked by ConsumerResolvedExemptions_AreStillActuallyResolved.
        consumerResolvedTypes.Should().Contain("ToolUseConfig",
            "control: PlanValidator runs IValidator<ToolUseConfig>, so it must be exempt here");

        // In scope unless a consumer is PROVEN to resolve it. An unparsable or unrecognised type
        // argument therefore stays in scope: unknown means "must be bound", so it surfaces as a
        // named failure to review instead of a silent exemption.
        var validatorNames = candidates
            .Where(c => c.Validated is null || !consumerResolvedTypes.Contains(c.Validated))
            .Select(c => c.Name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Control: the namespace filter must keep the config validators, or the guard passes by
        // scoping itself down to nothing — the same blind-guard failure one level further in.
        validatorNames.Should().Contain("GovernanceConfigValidator",
            "control: a validator over a bound configuration section must stay in scope");

        // Control: and it must exclude the runtime-payload shape, or #514's false alarm returns.
        // PlanValidator resolves these as IValidator<T>; PlanValidatorTests proves they run.
        validatorNames.Should().NotContain("ToolUseConfigValidator",
            "control: a validator over a runtime plan payload does not need an options binding");

        // Every DI file, not just the composition root: a binding is equally real in a subsystem's own
        // DependencyInjection partial, and reading one file reported anything registered elsewhere as
        // unbound. Source text rather than a resolved container, because an unbound validator is still
        // perfectly resolvable — that is exactly what made the original defect invisible.
        var wiringFiles = Directory
            .EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !SourceScan.IsExcluded(f, contentRoot))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).StartsWith("DependencyInjection", StringComparison.Ordinal)
                || Path.GetFileName(f).Equals("IServiceCollectionExtensions.cs", StringComparison.Ordinal))
            .ToArray();

        wiringFiles.Should().NotBeEmpty("the wiring files this reads must exist for its verdict to mean anything");

        var wiring = string.Join(
            "\n",
            wiringFiles.Select(f => SourceScan.StripCommentsAndStrings(File.ReadAllText(f))));

        // Control: the needle must match a binding known to be present, or "no offenders" would be
        // satisfied by a scan that cannot see any binding at all.
        wiring.Should().Contain("GovernanceConfigValidator",
            "control: a known-bound validator must be visible to this scan");

        var unbound = validatorNames
            .Where(name => !wiring.Contains(name, StringComparison.Ordinal))
            .ToArray();

        unbound.Should().BeEmpty(
            "a config validator that no AddOptions chain binds never runs, however thoroughly it is "
            + "tested and however confidently its doc comment says otherwise — AddValidatorsFromAssembly "
            + "registers it for resolution, it does not cause anything to resolve it. Either bind it in "
            + "RegisterValidatedConfigSections or delete it with the documentation claiming it enforces. "
            + "Unbound: " + string.Join(", ", unbound));
    }

    /// <summary>
    /// The single type argument of a validator file's <c>AbstractValidator&lt;T&gt;</c> base, or
    /// <see langword="null"/> when the file declares no such base, declares more than one, or names
    /// a type argument this scan cannot parse (qualified or generic).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> means <strong>unknown, therefore in scope</strong> — never excluded.
    /// Candidacy is decided upstream by the broad <c>: AbstractValidator&lt;</c> predicate, which is
    /// also what filters out the <c>IHostedService</c> startup validators; nothing is dropped here.
    /// A maintainer who "fixed" this to exclude on null would convert every unparsable type argument
    /// into a silent exemption, which is the one direction this guard must not fail in.
    /// </para>
    /// <para>
    /// More than one declaration in a file also yields <see langword="null"/> rather than the first
    /// match. No production file has two today, but a child validator declared beside its parent is
    /// ordinary FluentValidation, and taking the first would attribute the file-named validator's
    /// verdict to somebody else's type.
    /// </para>
    /// </remarks>
    private static string? ValidatedTypeName(string validatorFile)
    {
        var source = SourceScan.StripCommentsAndStrings(File.ReadAllText(validatorFile));
        var matches = Regex.Matches(source, @":\s*AbstractValidator\s*<\s*([A-Za-z0-9_]+)\s*>");
        return matches.Count == 1 ? matches[0].Groups[1].Value : null;
    }

    /// <summary>
    /// Validated types a production consumer resolves and runs itself, so no options binding is
    /// required — paired with the file that does it, so the exemption can be re-checked rather than
    /// trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit list, not a scan, because the dispatch it describes is generic and therefore
    /// unmatchable. <c>PlanValidator.ValidateConfig&lt;T&gt;</c> resolves
    /// <c>IValidator&lt;T&gt;</c> with an open type parameter; the concrete types appear only as
    /// switch patterns. There is no <c>IValidator&lt;ToolUseConfig&gt;</c> anywhere to anchor to.
    /// </para>
    /// <para>
    /// The previous attempt papered over that by treating "any <c>*Config</c>/<c>*Options</c>
    /// identifier in any file mentioning <c>IValidator&lt;</c>" as proof of a consumer. That is a
    /// different rule from the one its own name claimed, and a future resolver file mentioning an
    /// unrelated config type would have silently exempted it — the one direction this guard must not
    /// fail in, reached for the second time in two commits by trying to infer the answer instead of
    /// stating it. A short list that must be justified is the honest shape.
    /// </para>
    /// <para>
    /// <strong>Fail-closed by construction.</strong> Membership is the only exemption, so any new
    /// validator — over any type, in any namespace, parsable or not — is in scope until someone adds
    /// it here and says who runs it. <see cref="ConsumerResolvedExemptions_AreStillActuallyResolved"/>
    /// re-derives the justification on every run, so an entry whose consumer disappears fails rather
    /// than quietly keeping its exemption.
    /// </para>
    /// </remarks>
    private static readonly (string ValidatedType, string ResolvedBy)[] ConsumerResolvedValidatedTypes =
    [
        ("LlmCallConfig", "Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs"),
        ("ToolUseConfig", "Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs"),
        ("HumanGateConfig", "Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs"),
        ("ConditionalBranchConfig", "Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs"),
        ("SubPlanConfig", "Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs")
    ];

    /// <summary>
    /// Every exemption above must still be resolved by the file it names, or it is stale.
    /// </summary>
    /// <remarks>
    /// Without this, the list is an assertion that decays: a planner refactor that stopped running
    /// these validators would leave five permanently excused names behind, which is exactly the
    /// stale-allowlist failure the deleted <c>KnownUnboundConfigValidators</c> ratchet existed to
    /// prevent. This restores that property against the corrected premise.
    /// </remarks>
    [Fact]
    public void ConsumerResolvedExemptions_AreStillActuallyResolved()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        foreach (var (validatedType, resolvedBy) in ConsumerResolvedValidatedTypes)
        {
            var path = Path.Combine(contentRoot, resolvedBy.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"{resolvedBy} must exist to justify exempting {validatedType}");

            var source = SourceScan.StripCommentsAndStrings(File.ReadAllText(path));

            source.Should().Contain("IValidator<",
                $"{resolvedBy} must still resolve a validator, or {validatedType}'s exemption is stale");
            Regex.IsMatch(source, $@"\b{Regex.Escape(validatedType)}\b").Should().BeTrue(
                $"{resolvedBy} must still dispatch on {validatedType}, or its exemption is stale");
        }
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
