using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

public sealed partial class SecurityControlHasACallerTests
{
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
    /// so the exemption cannot outlive the defect it documents. Empty as of #531 — the egress
    /// manifest pair that populated it is now genuinely invoked (see
    /// <see cref="SkillEgressConsumerResolvedTypes"/>) — kept, not deleted, as the mechanism a future
    /// confirmed-dead validator uses.
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

        var consumerResolvedTypes = ConsumerResolvedValidatedTypes
            .Concat(SkillEgressConsumerResolvedTypes)
            .ToHashSet(StringComparer.Ordinal);
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

    // FindTypeDeclarations moved to Tests.Common/SourceScan.cs (#534 round 2) — it depends on no
    // fixture of this class, and this file was already well past the repo's file-size guideline.
    // Implements, FindMediatRRequestTypes, and FindValidatorDeclarations below call
    // SourceScan.FindTypeDeclarations directly.

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
    /// Entries are unqualified type names, so a second <c>SubPlanConfig</c> in another namespace
    /// would inherit this exemption — this repo already has a proven instance of the underlying
    /// hazard (<c>EscalationConfig</c> declared in two namespaces). <see cref="ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision"/>
    /// closes this (#528) by failing loudly if a second declaration ever appears for any of the six
    /// names, rather than trying to silently disambiguate which one the exemption "really" means. The
    /// filename-candidacy limit that used to be recorded here is gone — candidacy is now the
    /// <c>: AbstractValidator&lt;</c> shape, which is what closed #529 (four <c>Drift*Validator</c>
    /// classes registered by assembly scan, consumed by nothing, and outside the old scan's reach
    /// entirely).
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
    /// Every entry on <see cref="ConsumerResolvedValidatedTypes"/> must resolve to declarations in
    /// exactly one namespace, or the exemption is ambiguous (#528).
    /// </summary>
    /// <remarks>
    /// <see cref="FindValidatorDeclarations"/> deliberately extracts a <em>bare</em> type name from
    /// <c>AbstractValidator&lt;T&gt;</c> — a qualified argument (<c>Governance.EscalationConfig</c>)
    /// yields <see langword="null"/> and stays in scope, the fail-closed direction — and
    /// <see cref="ConsumerResolvedValidatedTypes"/>'s own entries are bare names for the same reason:
    /// there is nothing qualified on either side to compare against. That means a SECOND production
    /// type sharing one of these six bare names, in a different namespace, would make an unrelated
    /// validator over the OTHER type match the exemption too — silently, since
    /// <c>consumerResolvedTypes.Contains(c.Validated)</c> is a plain string comparison with no
    /// namespace awareness. This repo already has a proven instance of the underlying hazard —
    /// <c>EscalationConfig</c> is declared in two namespaces (see this file's own remarks above) — so
    /// the risk is not hypothetical, only not yet realized for these six names specifically.
    /// <para>
    /// Rather than attempt to disambiguate which of two same-named types "really" means the planner
    /// exemption — the same trap this file's history already names and rejects ("approximating a
    /// fact with something that correlates") — this fails loudly the moment a second declaration
    /// appears, forcing a human decision (rename one type, or qualify the validator declaration so it
    /// no longer matches the bare-name exemption at all) instead of silently trusting whichever one a
    /// regex happens to have found. Grouped by namespace rather than by file count specifically so a
    /// type legitimately split across <c>partial</c> declarations in the same namespace — this
    /// repo's own documented "Partial Class Pattern" — is not itself reported as a false collision.
    /// </para>
    /// <para>
    /// <strong>Positive and negative control:</strong> <see cref="FindDeclaringFilesByName_TwoFilesInDifferentNamespaces_BothNamespacesSurface"/>
    /// and <see cref="FindDeclaringFilesByName_PartialAcrossFilesInTheSameNamespace_OneNamespaceOnly"/>
    /// exercise the same <see cref="FindDeclaringFilesByName"/>/namespace-grouping this test uses,
    /// against synthetic (not real repo) source, precisely because none of the six real names
    /// currently collides — without a synthetic control, a mutation that silently disabled the
    /// offender-detection branch (for example widening <c>&gt;</c> to <c>&gt;=</c>, or breaking the
    /// <c>Distinct</c>) would never turn this test red.
    /// </para>
    /// <para>
    /// <strong>Known residual gaps, named rather than silently accepted:</strong> namespace-string
    /// equality is not CLR type identity — two same-named, same-namespace types declared in two
    /// different <c>.csproj</c> projects that are never referenced together by a call site using the
    /// bare name would compile cleanly and be (wrongly) grouped as one non-colliding declaration; a
    /// production file with more than one namespace block attributes every declaration in it to
    /// whichever namespace appears first in the file rather than the one actually enclosing each
    /// match (a repo-wide scan found zero of this repo's ~2,400 production files do this, so the gap
    /// is currently dormant, not live); and two declarations differing only by generic arity (a
    /// non-generic <c>Widget</c> and a hypothetical <c>Widget&lt;T&gt;</c>) are indistinguishable to
    /// the bare-name regex both this guard and <see cref="EveryValidator_HasAProvenInvocationMechanism"/>
    /// already share. Closing all three would need a real C# parser in place of this file's
    /// established crude-regex approach — out of proportion to #528's scope.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var sources = SourceScan.ReadProductionSources(contentRoot);
        sources.Should().NotBeEmpty("the production source this reads must exist for its verdict to mean anything");

        var declaringFilesByName = FindDeclaringFilesByName(sources, ConsumerResolvedValidatedTypes);

        // Control: at least one of the six names must actually resolve — if the declaration scan
        // matched nothing at all, every entry would trivially "have no collision" while also proving
        // nothing, the same vacuous-control shape this file's other guards already avoid.
        declaringFilesByName.Values.Any(files => files.Count > 0).Should().BeTrue(
            "the type-declaration scan must find at least one of the six consumer-resolved types, or "
            + "this test's namespace-collision check is passing vacuously");

        var offenders = declaringFilesByName
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => (TypeName: kv.Key, Files: kv.Value, Namespaces: NamespacesOf(kv.Value)))
            .Where(x => x.Namespaces.Length > 1)
            .Select(x =>
                $"'{x.TypeName}' declared in {x.Namespaces.Length} different namespaces "
                + $"({string.Join(", ", x.Namespaces)}) across: "
                + string.Join(", ", x.Files.Select(f => f.Path)))
            .ToArray();

        offenders.Should().BeEmpty(
            "each name is exempted from the options-binding guard as one of PlanValidator's "
            + "consumer-resolved step configs — a second production type sharing that bare name in a "
            + "DIFFERENT namespace would silently inherit the same exemption for an unrelated "
            + "validator, since the match has no namespace qualification on either side. Offenders: "
            + string.Join(" | ", offenders));
    }
}
