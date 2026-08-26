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
/// that existed, looked correct, had thorough unit tests, and was never invoked, five separate
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
    /// <strong>Scope: every validator, minus a stated exemption.</strong> There are two ways a
    /// validator runs. An <c>AddOptions</c> chain binds it to a configuration section — that is what
    /// this test checks for. Or a consumer resolves <c>IValidator&lt;T&gt;</c> itself and applies it,
    /// in which case the validator's own name appears in no wiring file while it runs on every call.
    /// The five planner step-config validators are the second shape:
    /// <c>PlanValidator.ValidateStepConfigurations</c> resolves and applies each one against every
    /// step of every plan. <c>PlanValidatorTests</c> proves the dispatch for three
    /// (<c>LlmCall</c>, <c>ToolUse</c>, <c>HumanGate</c> — each has an invalid-config test) and
    /// real-container resolution for two (<c>ToolUse</c>, <c>LlmCall</c>). <c>ConditionalBranch</c>
    /// and <c>SubPlan</c> are dispatched by the same switch but no test would fail if their arms
    /// were deleted — see #528.
    /// </para>
    /// <para>
    /// That second set is named explicitly in <see cref="ConsumerResolvedValidatedTypes"/> rather
    /// than inferred, because the dispatch is generic and there is no concrete
    /// <c>IValidator&lt;SomeConfig&gt;</c> anywhere to detect. Everything not on that list is in
    /// scope, so a new validator over any type in any namespace must be bound or reported.
    /// </para>
    /// <para>
    /// <strong>Five false alarms, and what each one taught.</strong> Matching on filename swept in
    /// two live <c>IHostedService</c> validators. Reading a single wiring file reported anything
    /// registered in a subsystem partial as unbound. Treating an options binding as the only
    /// invocation mechanism reported the five planner validators as dead debt (#514) when a prior
    /// audit had already proved they run. Then two attempts to <em>infer</em> the exemption failed in
    /// the opposite and more dangerous direction — a namespace rule that was simply false
    /// (<c>JudgeOptions</c> and friends are bound from <c>Application.AI.Common.Evaluation.Models</c>),
    /// and a scan that accepted any config-shaped identifier sharing a file with any
    /// <c>IValidator&lt;</c>. Both would have exempted a real unbound validator silently. The lesson
    /// this file kept relearning: when the mechanism cannot be detected, state it and check the
    /// statement, rather than approximating it with something that correlates.
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
        var consumerResolvedTypes = ConsumerResolvedValidatedTypes.ToHashSet(StringComparer.Ordinal);

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
        // right up until it reported five false alarms again. Whether each entry is STILL true is
        // not checked; see the remarks on ConsumerResolvedValidatedTypes for why.
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

        // Control: the exemption must not swallow a config validator, or the guard passes by
        // scoping itself down to nothing — the same blind-guard failure one level further in.
        validatorNames.Should().Contain("GovernanceConfigValidator",
            "control: a validator over a bound configuration section must stay in scope");

        // Control: and it must exclude the runtime-payload shape, or #514's false alarm returns.
        // PlanValidator resolves these as IValidator<T>; PlanValidatorTests proves the dispatch for
        // three of the five and real-container resolution for two (see the remarks above).
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
    /// match. No <c>*ConfigValidator.cs</c> candidate has two today, but the convention exists in
    /// this repo (<c>EgressManifestValidator.cs</c> declares a parent and a child), so taking the
    /// first would attribute the file-named validator's verdict to somebody else's type.
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
    /// required. <strong>Asserted, not enforced</strong> — see remarks before trusting it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicit list, not a scan, because the dispatch it describes is generic and therefore
    /// unmatchable. <c>PlanValidator.ValidateConfig&lt;T&gt;</c> resolves
    /// <c>IValidator&lt;T&gt;</c> with an open type parameter; the concrete types appear only as
    /// switch patterns in <c>ValidateStepConfigurations</c>. There is no
    /// <c>IValidator&lt;ToolUseConfig&gt;</c> anywhere to anchor a scan to.
    /// </para>
    /// <para>
    /// <strong>What this guarantees.</strong> Membership is the only exemption, so any new validator
    /// — over any type, in any namespace, parsable or not — is in scope until someone adds it here
    /// and says who runs it. That is the fail-closed direction, and it holds by construction.
    /// </para>
    /// <para>
    /// <strong>Staleness is enforced by <see cref="ConsumerResolvedExemptions_AreStillDispatched"/>,
    /// and the history of that check is instructive.</strong> Its first version matched the bare
    /// type name anywhere in <c>PlanValidator.cs</c>. That was blind for <c>SubPlanConfig</c>,
    /// which also appears in an unrelated method — and for the other four it worked. It was then
    /// withdrawn on the claim that it "could not fail", which was false: withdrawing it removed
    /// real detection for four of five, including <c>ConditionalBranchConfig</c>, which no other
    /// test guards. The restored check anchors on the dispatch-arm shape
    /// (<c>{Type} config =&gt; await ValidateConfig(</c>), which matches exactly the five arms and
    /// nothing else — verified — and its mutation test deletes the <c>SubPlanConfig</c> arm
    /// specifically, because that is the type with a second mention. The rest of the limits are
    /// tracked as #528.
    /// </para>
    /// <para>
    /// Nor does it prove the five are <em>registered</em>. <c>PlanValidator.ValidateConfig</c>
    /// fails open when no <c>IValidator&lt;T&gt;</c> resolves (#526), and their only registration
    /// is <c>AddValidatorsFromAssembly</c> on Application.Core; real-container resolution is tested
    /// for two of the five. The exemption says a consumer <em>would call</em> each one — not that
    /// one exists to be called.
    /// </para>
    /// <para>
    /// Two further limits, both tracked in #528. Candidacy is decided by the
    /// <c>*ConfigValidator.cs</c> filename, so a validator in a differently-named file is never
    /// scanned — and that is not hypothetical: four <c>Drift*Validator</c> classes under
    /// <c>Application.AI.Common/Interfaces/DriftDetection</c> are registered by assembly scan
    /// and consumed by nothing (#529). And entries are unqualified type names, so a second <c>SubPlanConfig</c> in another
    /// namespace would inherit this exemption.
    /// </para>
    /// </remarks>
    private static readonly string[] ConsumerResolvedValidatedTypes =
    [
        // All five: PlanValidator.ValidateStepConfigurations dispatches each to ValidateConfig<T>.
        "LlmCallConfig",
        "ToolUseConfig",
        "HumanGateConfig",
        "ConditionalBranchConfig",
        "SubPlanConfig"
    ];

    /// <summary>
    /// Every exemption on <see cref="ConsumerResolvedValidatedTypes"/> must still be a dispatch arm
    /// in <c>PlanValidator.ValidateStepConfigurations</c>, and every arm must be on the list.
    /// </summary>
    /// <remarks>
    /// Anchored on the arm shape, not the type name, so a mention elsewhere in the file cannot
    /// satisfy it (see the remarks on the list for why the first version was blind to exactly
    /// that). Checked both ways: a listed type with no arm is a stale exemption that would excuse
    /// an inert validator; an arm with no list entry is a validator the guard would wrongly
    /// report as unbound — #514's false alarm returning through the back door.
    /// </remarks>
    [Fact]
    public void ConsumerResolvedExemptions_AreStillDispatched()
    {
        var planValidator = Path.Combine(
            RepoRoot.Path, "src", "Content", "Infrastructure", "Infrastructure.AI", "Planner", "PlanValidator.cs");
        File.Exists(planValidator).Should().BeTrue("the consumer that justifies every exemption must exist");

        var source = SourceScan.StripCommentsAndStrings(File.ReadAllText(planValidator));
        var dispatched = Regex.Matches(source, @"\b(\w+Config)\s+\w+\s*=>\s*await\s+ValidateConfig\(")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Control: the anchor must find the arms, or an emptied dispatch would read as "nothing
        // stale" while every exemption had in fact gone dead.
        dispatched.Should().NotBeEmpty("the dispatch-arm anchor must match PlanValidator's switch");

        dispatched.Should().BeEquivalentTo(
            ConsumerResolvedValidatedTypes,
            "the exemption list and PlanValidator's dispatch arms must name the same types — a listed "
            + "type with no arm is a stale exemption excusing an inert validator; an arm with no entry "
            + "is a validator this guard would wrongly report as unbound");
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
