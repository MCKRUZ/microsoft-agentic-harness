using System.Text.RegularExpressions;
using Tests.Common;

namespace Presentation.Common.Tests.Composition;

public sealed partial class SecurityControlHasACallerTests
{
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
    /// <para>
    /// <see cref="SourceScan.FindTypeDeclarations"/> also finds <c>record</c>/<c>struct</c>
    /// declarations, but <c>AbstractValidator</c> is a plain <c>class</c>, which neither can ever
    /// legally derive from — that candidacy branch is permanently unreachable for this consumer
    /// specifically, confirmed against the real ~2,400-file production tree, and left as-is rather
    /// than narrowed: the shared parser stays one shape for all three consumers, and an unreachable
    /// branch here costs nothing at runtime.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string Validator, string? Validated)> FindValidatorDeclarations(
        string strippedSource)
    {
        var found = new List<(string, string?)>();

        // Candidacy over FindTypeDeclarations's base list, anchored to its START: a class can have at
        // most one base CLASS and C# requires it first in the list, so if AbstractValidator<T> is
        // present at all it is always the first thing after the colon — this anchor cannot miss a
        // real validator declared after some other base, because no such declaration is legal C#.
        //
        // The fail-closed property is unchanged and is worth restating: the optional trailing group
        // can only match a BARE identifier followed by '>', so a qualified argument
        // (Governance.EscalationConfig) or a generic one (Options<FooConfig>) leaves it unsuccessful,
        // yields null, and the declaration stays a candidate and stays IN scope. Unknown means "must
        // be bound".
        //
        // The optional (?:[\w.]+\.)? qualifier before AbstractValidator is load-bearing, exactly as it
        // is in IsRegistrationOnly below. Without it,
        // `class FooValidator : FluentValidation.AbstractValidator<FooConfig>` — how anyone
        // disambiguates a name clash, or writes it with no using directive — matches nothing and is
        // never a candidate at all. Not reported, not exempted, invisible: the same silent-invisibility
        // failure as the *ConfigValidator.cs filename rule this replaced (#529).
        foreach (var (name, baseList) in SourceScan.FindTypeDeclarations(strippedSource))
        {
            var match = Regex.Match(
                baseList, @"^\s*(?:[\w.]+\.)?AbstractValidator\s*<(?:\s*([A-Za-z0-9_]+)\s*>)?");

            if (match.Success)
                found.Add((name, match.Groups[1].Success ? match.Groups[1].Value : null));
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
    /// pipeline entirely — this was not hypothetical: <c>SkillTrainingExample</c> did exactly this
    /// with <c>TrainSkillCommand</c> until #533, which restored <c>TrainSkillCommandValidator</c>'s
    /// coverage on that path by invoking it explicitly (the demo can't dispatch through the real
    /// <c>IMediator</c> without losing its deterministic, LLM-free stubs — see the call site's own
    /// comment). The general risk remains real for any future direct-<c>Handle</c> caller. Detecting
    /// it needs call-graph analysis rather than a source scan, so it is stated rather than checked —
    /// the same honesty this file demands of the consumer-resolved exemption, which likewise proves a
    /// consumer <em>would</em> call each validator, not that one exists to be called.
    /// </para>
    /// <para>
    /// The base list is cut at <c>where</c> before matching (see <see cref="SourceScan.FindTypeDeclarations"/>) —
    /// provable in this repo, where <c>RequestValidationBehavior</c>'s own declaration ends
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
            foreach (var (name, baseList) in SourceScan.FindTypeDeclarations(code))
            {
                if (Regex.IsMatch(baseList, @"\bIRequest\b|\bIBaseRequest\b"))
                    requests.Add(name);
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
    /// Every file in <paramref name="sources"/> declaring (as <c>class</c>, <c>record</c>, or
    /// <c>struct</c>) any of <paramref name="typeNames"/>, grouped by the bare name matched.
    /// </summary>
    /// <remarks>
    /// One combined-alternation pass over the tree rather than one pass per name — <see cref="SourceScan"/>'s
    /// own remarks record a prior widened, repeated full-tree scan measurably destabilizing this
    /// suite's timing-sensitive sandbox-process tests (3/3 passing dropped to 1/3), so a second
    /// per-name scan loop here would reintroduce the same class of cost. Shared by
    /// <see cref="ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision"/> and
    /// <see cref="KnownDeadValidators_AreStillDead"/> so the "find declaring files by bare name" shape
    /// exists once rather than drifting between two independent inline regexes.
    /// </remarks>
    private static Dictionary<string, List<(string Path, string Code)>> FindDeclaringFilesByName(
        IEnumerable<(string Path, string Code)> sources, IReadOnlyList<string> typeNames)
    {
        var byName = typeNames.ToDictionary(n => n, _ => new List<(string Path, string Code)>(), StringComparer.Ordinal);
        if (typeNames.Count == 0)
            return byName;

        var pattern = $@"\b(?:class|record|struct)\s+({string.Join("|", typeNames.Select(Regex.Escape))})\b";

        foreach (var source in sources)
        {
            foreach (var matchedName in Regex.Matches(source.Code, pattern)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal))
            {
                byName[matchedName].Add(source);
            }
        }

        return byName;
    }

    /// <summary>
    /// The distinct namespaces declaring <paramref name="files"/>, in file order. Takes the first
    /// <c>namespace</c> match per file — a dormant gap for a file with more than one namespace block,
    /// see the residual-gaps remarks on <see cref="ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision"/>.
    /// </summary>
    private static string[] NamespacesOf(IEnumerable<(string Path, string Code)> files) =>
        files
            .Select(f => Regex.Match(f.Code, @"namespace\s+([\w.]+)\s*[{;]") is { Success: true } m
                ? m.Groups[1].Value
                : "<no namespace>")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
    /// <remarks>
    /// Over <see cref="SourceScan.FindTypeDeclarations"/>'s base list rather than its own pattern
    /// (#534) — the original pattern had no primary-constructor group, so
    /// <c>record Foo(string Id) : IContract</c> matched nothing and the declaring file was counted as
    /// a CONSUMER of the contract instead of an implementation: the dangerous direction, since it
    /// makes an uncalled governance contract look called. It also had no <c>where</c> cut, so
    /// <c>class Wrapper&lt;T&gt; : Base where T : IContract</c> was wrongly read as an implementation
    /// of <c>IContract</c> — a false alarm rather than a silent pass, but still wrong; both are fixed
    /// by sharing <see cref="SourceScan.FindTypeDeclarations"/>'s parser instead of a third,
    /// independent pattern.
    /// </remarks>
    private static bool Implements(string code, string contract) =>
        SourceScan.FindTypeDeclarations(code).Any(d => Regex.IsMatch(d.BaseList, $@"\b{contract}\b"));
}
