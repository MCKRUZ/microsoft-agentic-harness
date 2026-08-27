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
/// <em>interfaces</em> declared under the guarded folders, so of the five defects named above it could
/// only ever have caught the last. The other four are concrete classes implementing no guarded
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

        var productionFiles = SourceScan.ReadProductionSources(contentRoot);

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
    /// Every FluentValidation validator in the repo must have a proven invocation mechanism, because a
    /// validator nothing invokes runs nowhere and enforces nothing.
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
    /// <strong>Scope: every validator, minus a stated exemption.</strong> There are three ways a
    /// validator runs, and candidacy is now decided by the <em>shape</em> that needs wiring
    /// (<c>: AbstractValidator&lt;T&gt;</c>) rather than by a filename convention — see the false-alarm
    /// history below for why that change was forced. First, an <c>AddOptions</c> chain binds it to a
    /// configuration section — that is what this test checks for. Second, MediatR's
    /// <c>RequestValidationBehavior&lt;TRequest, TResponse&gt;</c> injects
    /// <c>IEnumerable&lt;IValidator&lt;TRequest&gt;&gt;</c> and runs every one, so any validator whose
    /// validated type is itself a MediatR request needs no binding of its own; that premise is not
    /// assumed but asserted by <see cref="MediatRValidationBehavior_IsRegisteredAsAnOpenGenericPipelineBehavior"/>,
    /// because it is the claim 58 exemptions rest on. Third, a consumer resolves
    /// <c>IValidator&lt;T&gt;</c> itself and applies it, in which case the validator's own name appears
    /// in no wiring file while it runs on every call. The six planner step-config validators are the
    /// third shape:
    /// <c>PlanValidator.ValidateStepConfigurations</c> resolves and applies each one against every
    /// step of every plan. <c>PlanValidatorTests</c> proves the dispatch for three
    /// (<c>LlmCall</c>, <c>ToolUse</c>, <c>HumanGate</c> — each has an invalid-config test) and
    /// real-container resolution for two (<c>ToolUse</c>, <c>LlmCall</c>). <c>ConditionalBranch</c>
    /// and <c>SubPlan</c> have no invalid-config test of their own, so their dispatch is guarded
    /// only by <see cref="ConsumerResolvedExemptions_AreStillDispatched"/> below — which does fail
    /// if either arm is deleted. A per-type invalid-config test would additionally prove the
    /// validator is registered, not merely dispatched; that half is #528.
    /// </para>
    /// <para>
    /// That third set is named explicitly in <see cref="ConsumerResolvedValidatedTypes"/> rather
    /// than inferred, because the dispatch is generic and there is no concrete
    /// <c>IValidator&lt;SomeConfig&gt;</c> anywhere to detect. Everything not on that list is in
    /// scope, so a new validator over any type in any namespace must be bound or reported.
    /// </para>
    /// <para>
    /// <strong>A fourth list, deliberately kept separate: <see cref="KnownDeadValidators"/>.</strong>
    /// Those are validators confirmed to have no caller, carried under a tracked issue rather than
    /// silently skipped. It is a different meaning from "a consumer resolves this", so it is a
    /// different list — the same reasoning CLAUDE.md records for not overloading one field with two
    /// meanings. <see cref="KnownDeadValidators_AreStillDead"/> fails the moment one gains a caller,
    /// so the exemption cannot outlive the defect it documents.
    /// </para>
    /// <para>
    /// <strong>Six false alarms, and what each one taught.</strong> Matching on filename swept in
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
    /// <para>
    /// The sixth was this guard's own <em>candidacy</em> rule, and it failed in the dangerous
    /// direction across every revision above. Enumerating <c>*ConfigValidator.cs</c> by filename meant
    /// a validator in a differently-named file was never scanned at all — not reported, not exempted,
    /// simply invisible. #529 proved it live: four <c>Drift*Validator</c> classes registered by
    /// assembly scan and consumed by nothing, sitting outside the scan while five separate revisions
    /// argued about the exemption list. Candidacy is now the shape that actually needs wiring,
    /// <c>: AbstractValidator&lt;</c>, which is what the very first false alarm was already pointing
    /// at from the other side. Widening it immediately surfaced a second real instance the old rule
    /// could never have seen — the egress manifest pair, now #531.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryValidator_HasAProvenInvocationMechanism()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        var sources = SourceScan.ReadProductionSources(contentRoot);

        sources.Should().NotBeEmpty("the production source this reads must exist for its verdict to mean anything");

        // Candidacy is the shape that needs wiring, not a filename. The previous rule enumerated
        // *ConfigValidator.cs, which left every differently-named validator outside the scan entirely
        // — invisible rather than reported (#529, #531). The AbstractValidator base is also what
        // excludes the IHostedService startup validators, which self-register through AddHostedService
        // and need no options binding: matching on name alone reported two live controls as dead debt,
        // and a guard that cries wolf is the fastest way to get one ignored.
        var candidates = sources
            .SelectMany(s => FindValidatorDeclarations(s.Code))
            .ToArray();

        candidates.Should().NotBeEmpty("the validators this reads must exist for its verdict to mean anything");

        // Control: the IHostedService shape must still be excluded, or the original false alarm
        // returns. A NotContain alone passes vacuously once the class is renamed or deleted — the
        // exclusion property would silently stop being proven with nothing failing — so assert the
        // subject still exists first. The two Contain controls below need no such companion.
        sources.Should().Contain(
            s => Regex.IsMatch(s.Code, @"\bclass\s+ToolAuthorizationConfigValidator\b"),
            "control subject: the IHostedService validator this control excludes must still exist, or "
            + "the NotContain below proves nothing");

        candidates.Should().NotContain(c => c.Validator == "ToolAuthorizationConfigValidator",
            "control: an IHostedService validator must not be treated as needing an options binding");

        // Control, covering two properties at once. EgressManifestValidator.cs sits outside the old
        // *ConfigValidator.cs naming convention, so seeing it at all proves candidacy is shape-based;
        // and it declares BOTH a parent and a child validator, so seeing both proves attribution is
        // per-declaration rather than per-file.
        //
        // Deliberately BeEquivalentTo rather than a Contain plus a Count of two: that pair is
        // satisfiable by counting one name twice while the other is missing, which is the same
        // vacuous-control shape this file just had to fix in the NotContain above.
        candidates.Select(c => c.Validator)
            .Where(n => n is "EgressManifestValidator" or "EgressAllowlistEntryValidator")
            .Should().BeEquivalentTo(
                ["EgressManifestValidator", "EgressAllowlistEntryValidator"],
                "control: a validator outside the *ConfigValidator.cs naming convention must be in "
                + "scope, and both declarations in a two-validator file must be attributed separately");

        var mediatrRequests = FindMediatRRequestTypes(sources);

        // Control: the MediatR classifier must actually find request types. If it found none, every
        // command/query validator would fall through to "must be bound" — the fail-closed direction,
        // but it would surface as dozens of false alarms, so prove the classifier works.
        mediatrRequests.Should().NotBeEmpty("the MediatR request types this exemption rests on must be detectable");

        var consumerResolvedTypes = ConsumerResolvedValidatedTypes.ToHashSet(StringComparer.Ordinal);
        var knownDead = KnownDeadValidators.ToHashSet(StringComparer.Ordinal);

        // In scope unless an invocation mechanism is PROVEN. An unparsable or unrecognised type
        // argument therefore stays in scope: unknown means "must be bound", so it surfaces as a named
        // failure to review instead of a silent exemption.
        var needsBinding = candidates
            .Where(c => !knownDead.Contains(c.Validator))
            .Where(c => c.Validated is null
                || (!mediatrRequests.Contains(c.Validated)
                    && !consumerResolvedTypes.Contains(c.Validated)))
            .Select(c => c.Validator)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Control: the exemptions must not swallow a config validator, or the guard passes by scoping
        // itself down to nothing — the blind-guard failure one level further in.
        needsBinding.Should().Contain("GovernanceConfigValidator",
            "control: a validator over a bound configuration section must stay in scope");

        // Control: the runtime-payload shape stays out, or #514's false alarm returns.
        needsBinding.Should().NotContain("ToolUseConfigValidator",
            "control: a validator over a runtime plan payload does not need an options binding");

        // Control: the MediatR exemption must actually exempt. This validator runs on every drift push
        // via RequestValidationBehavior and binds to no configuration section.
        needsBinding.Should().NotContain("PushDriftEvaluationCommandValidator",
            "control: a validator over a MediatR request is run by RequestValidationBehavior");

        // Every DI file, not just the composition root: a binding is equally real in a subsystem's own
        // DependencyInjection partial, and reading one file reported anything registered elsewhere as
        // unbound. Source text rather than a resolved container, because an unbound validator is still
        // perfectly resolvable — that is exactly what made the original defect invisible.
        // Both matched by PREFIX. IServiceCollectionExtensions was previously matched by exact
        // filename, which was harmless while any mention of a validator anywhere counted as a
        // binding, and is not any more: all twenty real bindings live in that one file, so this
        // predicate is now the sole load-bearing path to them. This repo's own convention is to split
        // registration files into partials (DependencyInjection.Governance.cs, .Identity.cs, ...), so
        // an IServiceCollectionExtensions.Validation.cs is a natural refactor that would have dropped
        // all twenty bindings at once and failed the guard with twenty names.
        var wiringFiles = sources
            .Where(s => Path.GetFileName(s.Path).StartsWith("DependencyInjection", StringComparison.Ordinal)
                || Path.GetFileName(s.Path).StartsWith("IServiceCollectionExtensions", StringComparison.Ordinal))
            .ToArray();

        wiringFiles.Should().NotBeEmpty("the wiring files this reads must exist for its verdict to mean anything");

        var wiring = string.Join("\n", wiringFiles.Select(s => s.Code));
        var bound = FindOptionsBoundValidators(wiring);

        // Control: the needle must match a binding known to be present, or "no offenders" would be
        // satisfied by a scan that cannot see any binding at all.
        bound.Should().Contain("GovernanceConfigValidator",
            "control: a known-bound validator must be visible to this scan");

        // Control: the wrapped call form must parse too. DriftDetectionConfigValidator is bound by a
        // ValidateFluentValidation< whose type arguments sit on the next two lines; a line-anchored
        // pattern sees only the single-line form and reports the wrapped ones as unbound.
        bound.Should().Contain("DriftDetectionConfigValidator",
            "control: a binding whose type arguments wrap across lines must still be credited");

        var unbound = needsBinding
            .Where(name => !bound.Contains(name))
            .ToArray();

        unbound.Should().BeEmpty(
            "a validator that no AddOptions chain binds, no MediatR request carries, and no consumer "
            + "resolves never runs, however thoroughly it is tested and however confidently its doc "
            + "comment says otherwise — AddValidatorsFromAssembly registers it for resolution, it does "
            + "not cause anything to resolve it, and neither does adding an AddSingleton<IValidator<T>> "
            + "of your own: only a ValidateFluentValidation<TConfig, TValidator> chain counts here. "
            + "Either bind it in RegisterValidatedConfigSections, give it a consumer and record which "
            + "one, or delete it along with the documentation claiming it enforces. "
            + "Unbound: " + string.Join(", ", unbound));
    }

    /// <summary>
    /// The MediatR validation behavior's registration, asserted rather than assumed.
    /// </summary>
    /// <remarks>
    /// <see cref="EveryValidator_HasAProvenInvocationMechanism"/> exempts every validator whose
    /// validated type is a MediatR request, on the premise that
    /// <c>RequestValidationBehavior&lt;TRequest, TResponse&gt;</c> is registered as an open-generic
    /// pipeline behavior and resolves <c>IEnumerable&lt;IValidator&lt;TRequest&gt;&gt;</c>. That is by
    /// far the largest exemption in this file. Delete the registration line and every one of those
    /// validators silently stops running while their own unit tests keep passing — precisely the defect
    /// shape this file exists to catch, so the premise gets a check rather than a comment.
    /// </remarks>
    [Fact]
    public void MediatRValidationBehavior_IsRegisteredAsAnOpenGenericPipelineBehavior()
    {
        var registration = Path.Combine(
            RepoRoot.Path, "src", "Content", "Application", "Application.Common", "DependencyInjection.cs");
        File.Exists(registration).Should().BeTrue("the registration that justifies the MediatR exemption must exist");

        var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(registration));

        Regex.IsMatch(
                code,
                @"AddTransient\s*\(\s*typeof\s*\(\s*IPipelineBehavior<,>\s*\)\s*,\s*typeof\s*\(\s*RequestValidationBehavior<,>\s*\)\s*\)")
            .Should().BeTrue(
                "the MediatR exemption in EveryValidator_HasAProvenInvocationMechanism assumes every "
                + "IValidator<TRequest> is run by RequestValidationBehavior. Without this open-generic "
                + "registration that assumption is false and every command and query validator in the "
                + "repo is inert while its unit tests still pass.");

        var behavior = Path.Combine(
            RepoRoot.Path, "src", "Content", "Application", "Application.Common",
            "MediatRBehaviors", "RequestValidationBehavior.cs");
        File.Exists(behavior).Should().BeTrue("the behavior that justifies the MediatR exemption must exist");

        // Registration alone is not enough: a behavior that no longer resolves the validators would
        // satisfy the check above while running none of them.
        SourceScan.StripCommentsAndStrings(File.ReadAllText(behavior))
            .Should().Contain("IEnumerable<IValidator<TRequest>>",
                "the behavior must still resolve the validators this exemption credits it with running");

        // And resolving them is not enough either: the collection the behavior injects is populated
        // solely by AddValidatorsFromAssembly. Delete one of these and every validator in that
        // assembly silently stops running — the MediatR exemptions here AND the consumer-resolved
        // ones, since PlanValidator.ValidateConfig fails open when nothing resolves (#526). Nothing
        // throws; the tests all still pass. Apply this file's own standing question — which single
        // line, if deleted, restores the unguarded behaviour, and does a test fail when it is gone? —
        // and without this assertion the answer for the registration half was: no test fails.
        string[] validatorRegistrations =
        [
            Path.Combine("Application", "Application.Common", "DependencyInjection.cs"),
            Path.Combine("Application", "Application.AI.Common", "DependencyInjection.cs"),
            Path.Combine("Application", "Application.Core", "DependencyInjection.cs")
        ];

        foreach (var relative in validatorRegistrations)
        {
            var path = Path.Combine(RepoRoot.Path, "src", "Content", relative);
            File.Exists(path).Should().BeTrue($"{relative} must exist to register its assembly's validators");

            SourceScan.StripCommentsAndStrings(File.ReadAllText(path))
                .Should().Contain("AddValidatorsFromAssembly",
                    $"{relative} is what puts its assembly's IValidator<T> registrations in the "
                    + "container. Without it, RequestValidationBehavior resolves an empty collection "
                    + "and every exemption this guard grants over that assembly becomes a silent lie.");
        }
    }

    /// <summary>
    /// Every <c>class X : AbstractValidator&lt;T&gt;</c> declared in one file, as
    /// (validator name, validated type), with the validated type <see langword="null"/> when it cannot
    /// be parsed as a bare identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> means <strong>unknown, therefore in scope</strong> — never excluded. A
    /// maintainer who "fixed" this to drop unparsable declarations would convert every qualified or
    /// generic type argument into a silent exemption, which is the one direction this guard must not
    /// fail in. That is not hypothetical: <c>EscalationConfig</c> is declared in two namespaces, so the
    /// natural disambiguation <c>AbstractValidator&lt;Governance.EscalationConfig&gt;</c> would make
    /// <c>EscalationConfigValidator</c> vanish and let #516's defect land again unseen.
    /// </para>
    /// <para>
    /// Attribution is per declaration, not per file. The previous version keyed on the filename and
    /// returned <see langword="null"/> whenever a file declared more than one validator — tolerable
    /// only because such files sat outside its filename-based candidacy. Now that candidacy is
    /// shape-based they are in scope, and <c>EgressManifestValidator.cs</c> declares two; attributing
    /// both to a single name would drop a real validator from the scan.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string Validator, string? Validated)> FindValidatorDeclarations(
        string strippedSource)
    {
        var found = new List<(string, string?)>();

        // ONE pattern, with the type argument in an optional trailing group. Candidacy is everything
        // up to the opening angle bracket; attribution is the optional group. A first cut used two
        // separate regexes — a broad one for candidacy, a precise one re-matched against a substring
        // — which meant editing one and not the other would let candidacy and attribution disagree
        // silently, the exact failure this method's remarks warn about.
        //
        // The fail-closed property is unchanged and is worth restating: the optional group can only
        // match a BARE identifier followed by '>', so a qualified argument (Governance.EscalationConfig)
        // or a generic one (Options<FooConfig>) leaves it unsuccessful, yields null, and the
        // declaration stays a candidate and stays IN scope. Unknown means "must be bound".
        //
        // The optional (?:[\w.]+\.)? qualifier before AbstractValidator is load-bearing, exactly as it
        // is in IsRegistrationOnly below. Without it,
        // `class FooValidator : FluentValidation.AbstractValidator<FooConfig>` — how anyone
        // disambiguates a name clash, or writes it with no using directive — matches nothing and is
        // never a candidate at all. Not reported, not exempted, invisible: the same silent-invisibility
        // failure as the *ConfigValidator.cs filename rule this replaced (#529).
        var declarations = Regex.Matches(
            strippedSource,
            @"\bclass\s+(\w+)\s*(?:<[^>]*>)?\s*:\s*(?:[\w.]+\.)?AbstractValidator\s*<(?:\s*([A-Za-z0-9_]+)\s*>)?");

        foreach (Match declaration in declarations)
        {
            found.Add((
                declaration.Groups[1].Value,
                declaration.Groups[2].Success ? declaration.Groups[2].Value : null));
        }

        return found;
    }

    /// <summary>
    /// Every type declared in production source whose base list names a MediatR request contract, and
    /// which is therefore validated by <c>RequestValidationBehavior</c> with no binding of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detected, not assumed. The repo derives its commands and queries directly from
    /// <c>IRequest</c>/<c>IRequest&lt;T&gt;</c> rather than through a local alias interface, so this
    /// matches the real shape. If an alias is ever introduced, the validators over it fall through to
    /// "must be bound" and surface as named failures rather than silent exemptions — the fail-closed
    /// direction. A type this cannot find at all is likewise treated as not-a-request.
    /// </para>
    /// <para>
    /// <strong><c>INotification</c> is deliberately NOT here.</strong> A first cut included it, which
    /// was wrong in the one direction this file must never fail in.
    /// <c>RequestValidationBehavior</c> is an <c>IPipelineBehavior</c>, and MediatR runs pipeline
    /// behaviors only on <c>Send</c>; <c>Publish</c> dispatches straight to
    /// <c>INotificationHandler</c> with no pipeline at all. Crediting a notification type would have
    /// silently exempted a validator that genuinely never runs — #516's defect, introduced by the
    /// guard written to catch it. Latent today (the repo declares no production <c>INotification</c>),
    /// which is exactly why it needed catching before it was not.
    /// </para>
    /// <para>
    /// <strong>What this exemption does NOT prove.</strong> It says the type is <em>dispatchable</em>
    /// through MediatR, so a <c>Send</c> would run its validator. It does not prove every caller uses
    /// <c>Send</c>. A caller that constructs a handler and invokes <c>Handle</c> directly bypasses the
    /// pipeline entirely, and that is not hypothetical — <c>SkillTrainingExample</c> does exactly this
    /// with <c>TrainSkillCommand</c>, so <c>TrainSkillCommandValidator</c> is certified here while
    /// that particular path runs none of its rules. Harmless there (the example's config is
    /// hardcoded valid) but a pattern a template consumer could copy. Detecting it needs call-graph
    /// analysis rather than a source scan, so it is stated rather than checked — the same honesty this
    /// file demands of the consumer-resolved exemption, which likewise proves a consumer <em>would</em>
    /// call each validator, not that one exists to be called.
    /// </para>
    /// <para>
    /// The base list is cut at <c>where</c> before matching. The capture runs to the end of the line,
    /// and a generic constraint sits on that same line — provable in this repo, where
    /// <c>RequestValidationBehavior</c>'s own declaration ends
    /// <c>: IPipelineBehavior&lt;TRequest, TResponse&gt; where TRequest : notnull</c>. Without the
    /// cut, <c>class Envelope&lt;T&gt; : Base&lt;T&gt; where T : IRequest</c> would register
    /// <c>Envelope</c> as a MediatR request and silently exempt any validator over it.
    /// </para>
    /// </remarks>
    private static HashSet<string> FindMediatRRequestTypes(
        IReadOnlyList<(string Path, string Code)> sources)
    {
        var requests = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, code) in sources)
        {
            foreach (Match declaration in Regex.Matches(
                code,
                @"\b(?:record|class|struct)\s+(\w+)\s*(?:<[^>]*>)?\s*(?:\([^)]*\))?\s*:\s*([^{\r\n]+)"))
            {
                // Constraints are not base types. Everything from `where` onward describes what a
                // type parameter must satisfy, not what this type derives from.
                var baseList = Regex.Split(declaration.Groups[2].Value, @"\bwhere\b")[0];

                if (Regex.IsMatch(baseList, @"\bIRequest\b|\bIBaseRequest\b"))
                    requests.Add(declaration.Groups[1].Value);
            }
        }

        return requests;
    }

    /// <summary>
    /// Validator names an <c>AddOptions</c> chain actually binds, read from the
    /// <c>ValidateFluentValidation&lt;TConfig, TValidator&gt;</c> calls themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare mention is not a binding. The previous check credited any occurrence of the validator's
    /// name anywhere in a DI file, so
    /// <c>services.AddSingleton&lt;IValidator&lt;FooConfig&gt;, FooConfigValidator&gt;()</c> — a
    /// registration that causes nothing to resolve it — satisfied a guard whose failure message tells
    /// you to add an options binding. That is #516's exact defect passing the check written to catch
    /// it. Candidacy widening from a filename match to all 85 validators grew the surface that
    /// looseness applies to, so it is tightened here rather than left as-is.
    /// </para>
    /// <para>
    /// <strong>Must span newlines.</strong> These calls wrap: four of the twenty in-scope validators
    /// are bound by a <c>ValidateFluentValidation&lt;</c> whose type arguments sit on the following
    /// two lines. A line-anchored pattern silently misses those four and reports them as unbound —
    /// that mistake was made while verifying this very finding, and briefly led to rejecting it.
    /// </para>
    /// <para>
    /// <strong>Why depth-scanning rather than a regex.</strong> A first cut matched
    /// <c>&lt;[^&lt;&gt;]*,\s*([A-Za-z0-9_]+)&gt;</c>, which fails toward a false alarm in two real
    /// shapes: a namespace-qualified validator (<c>Governance.EscalationConfigValidator</c> — and this
    /// repo already qualifies the *config* argument for precisely that reason, because
    /// <c>EscalationConfig</c> exists in two namespaces) and a generic config argument
    /// (<c>Options&lt;FooConfig&gt;</c>), which <c>[^&lt;&gt;]</c> cannot cross. Reporting a correctly
    /// bound validator as unbound is how a guard gets ignored — this file's remarks say so in three
    /// places, and it is a regression tightening the check introduced. Counting bracket depth handles
    /// both without pretending a regex can balance brackets.
    /// </para>
    /// </remarks>
    private static HashSet<string> FindOptionsBoundValidators(string wiring)
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match call in Regex.Matches(wiring, @"\bValidateFluentValidation\s*<"))
        {
            // The pattern ends in '<' and can contain no other, so the match's own end IS the opening
            // bracket — no second scan for it.
            var open = call.Index + call.Length - 1;
            var depth = 0;
            var lastTopLevelComma = -1;
            int i;

            for (i = open; i < wiring.Length; i++)
            {
                if (wiring[i] == '<') depth++;
                else if (wiring[i] == '>' && --depth == 0) break;
                else if (wiring[i] == ',' && depth == 1) lastTopLevelComma = i;
            }

            // An unbalanced call means the scan lost its place; skip it rather than guess, so the
            // validator stays in scope and surfaces as a named failure.
            if (i >= wiring.Length || lastTopLevelComma < 0)
                continue;

            // The last top-level argument is TValidator. Strip any namespace qualifier — LastIndexOf
            // returns -1 when there is none, so the +1 leaves the whole string intact.
            var validator = wiring[(lastTopLevelComma + 1)..i].Trim();
            validator = validator[(validator.LastIndexOf('.') + 1)..];

            if (validator.Length > 0)
                bound.Add(validator);
        }

        return bound;
    }

    /// <summary>
    /// Validated types a production consumer resolves and runs itself, so no options binding is
    /// required. Kept honest by <see cref="ConsumerResolvedExemptions_AreStillDispatched"/>; read
    /// the remarks for what that does and does not cover.
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
    /// (<c>{Type} config =&gt; await ValidateConfig(</c>) rather than on the type's name, which
    /// currently matches exactly six arms and nothing else — verified — and its mutation test
    /// deletes the <c>SubPlanConfig</c> arm specifically, because that is the type with a second
    /// mention. The regex originally required its captured type name to end in "Config"; #526
    /// dropped that requirement after finding it blind to <c>RetrievalStepConfiguration</c>, whose
    /// name ends in "Configuration" — a live blind spot in the same family this file's history is
    /// otherwise all about, found by adding a type rather than by review. The rest of the limits are
    /// tracked as #528.
    /// </para>
    /// <para>
    /// <c>PlanValidator.ValidateConfig</c> used to fail open when no <c>IValidator&lt;T&gt;</c>
    /// resolved; #526 made that fail closed, and
    /// <c>PlannerStepConfigValidatorWiringTests.RealContainer_ResolvesAValidatorForEveryKnownStepConfigurationType</c>
    /// now resolves all six from a real container built the way the composition root builds it — not
    /// mocked, not partial — so this exemption list and that test together prove both halves: a
    /// consumer would call each one, and each one exists to be called.
    /// </para>
    /// <para>
    /// One limit remains, tracked in #528: entries are unqualified type names, so a second
    /// <c>SubPlanConfig</c> in another namespace would inherit this exemption. The filename-candidacy
    /// limit that used to be recorded here is gone — candidacy is now the <c>: AbstractValidator&lt;</c>
    /// shape, which is what closed #529 (four <c>Drift*Validator</c> classes registered by assembly
    /// scan, consumed by nothing, and outside the old scan's reach entirely).
    /// </para>
    /// </remarks>
    private static readonly string[] ConsumerResolvedValidatedTypes =
    [
        // All six: PlanValidator.ValidateStepConfigurations dispatches each to ValidateConfig<T>.
        // RetrievalStepConfiguration added by #526 — it had no switch arm and no validator at all
        // before that fix, which is the reason the anchor below no longer requires a "Config" suffix.
        "LlmCallConfig",
        "ToolUseConfig",
        "HumanGateConfig",
        "ConditionalBranchConfig",
        "SubPlanConfig",
        "RetrievalStepConfiguration"
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

        // Deliberately NOT anchored on a "Config" type-name suffix. #526 added
        // RetrievalStepConfiguration, whose name ends in "Configuration" — a suffix-anchored version
        // of this pattern would have been silently blind to it in exactly the way the file's own
        // remarks describe for the pre-2026-08-26 candidacy rule elsewhere in this suite: the guard
        // would report nothing wrong while a real arm went unwatched. The dispatch SHAPE
        // ({Type} config => await ValidateConfig() is what identifies an arm; the type's name is not
        // load-bearing and must never be part of the anchor again.
        var dispatched = Regex.Matches(source, @"\b(\w+)\s+\w+\s*=>\s*await\s+ValidateConfig\(")
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
    /// Validators confirmed to have no caller, carried openly under a tracked issue rather than
    /// silently skipped. Keyed by validator name, not validated type — the exemption is about a
    /// specific dead class, which is a different fact from
    /// <see cref="ConsumerResolvedValidatedTypes"/>'s "a consumer runs this type".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two lists rather than one because they mean different things, and CLAUDE.md records what
    /// happens when one field is made to carry two meanings: the reader cannot tell which case they
    /// are looking at, and the next maintainer picks the wrong one. "Nothing calls this, and we know"
    /// must never be spelled the same way as "something calls this".
    /// </para>
    /// <para>
    /// This is <strong>not</strong> a place to park an inconvenient failure. An entry is admissible
    /// only with a filed issue and a measured reason the defect is not urgent. The egress pair
    /// qualifies on both counts: their validated types are parsed onto
    /// <c>SkillDefinition.Egress</c> and read by nothing, so validating them would change no
    /// behaviour, and the runtime <c>DefaultEgressPolicy</c> still enforces scheme and allowlist
    /// matching on every request — the gap fails closed. #531 carries the decision of whether to wire
    /// per-skill egress or remove it.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownDeadValidators =
    [
        // #531: SkillDefinition.Egress is parsed, mapped, stored, and consumed by nothing, so neither
        // validator has anything to validate. Fails closed — DefaultEgressPolicy still enforces.
        "EgressManifestValidator",
        "EgressAllowlistEntryValidator"
    ];

    /// <summary>
    /// Every entry on <see cref="KnownDeadValidators"/> must still be dead, so the exemption cannot
    /// outlive the defect it documents.
    /// </summary>
    /// <remarks>
    /// Fails in the direction that matters. The moment someone wires one of these — which is the fix
    /// #531 asks for — this test reports that the exemption is now false and must be removed, rather
    /// than letting a newly-live validator sit permanently outside the guard's scope. A dead-control
    /// exemption that survives the control coming alive is how the scope of a guard quietly shrinks.
    /// </remarks>
    [Fact]
    public void KnownDeadValidators_AreStillDead()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        var production = SourceScan.ReadProductionSources(contentRoot);

        production.Should().NotBeEmpty("the production source this reads must exist for its verdict to mean anything");

        var revived = new List<string>();

        foreach (var validator in KnownDeadValidators)
        {
            // A caller is any production mention outside the file that declares it. The declaring file
            // is excluded because a parent validator legitimately names its child via SetValidator,
            // which is self-reference, not a consumer.
            var declaring = production
                .Where(f => Regex.IsMatch(f.Code, $@"\bclass\s+{validator}\b"))
                .Select(f => f.Path)
                .ToArray();

            declaring.Should().ContainSingle(
                $"{validator} must still be declared exactly once for this exemption to describe anything real");

            var callers = production
                .Where(f => !string.Equals(f.Path, declaring[0], StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(f.Code, $@"\b{validator}\b"))
                .Select(f => Path.GetRelativePath(contentRoot, f.Path))
                .ToArray();

            if (callers.Length > 0)
                revived.Add($"{validator} (now referenced by {string.Join(", ", callers)})");
        }

        revived.Should().BeEmpty(
            "a validator on KnownDeadValidators has gained a production caller, so the exemption that "
            + "excused it is now false and is holding a live validator outside this guard's scope. "
            + "Remove the entry — and if this is the #531 fix landing, remove both. Revived: "
            + string.Join("; ", revived));
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
