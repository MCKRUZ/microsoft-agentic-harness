using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

public sealed partial class SecurityControlHasACallerTests
{
    /// <summary>
    /// Positive control for <see cref="ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision"/>,
    /// against fabricated source so it does not depend on the real repo ever containing a live
    /// collision for one of the six exempted names.
    /// </summary>
    [Fact]
    public void FindDeclaringFilesByName_TwoFilesInDifferentNamespaces_BothNamespacesSurface()
    {
        var synthetic = new (string Path, string Code)[]
        {
            ("A.cs", "namespace Foo.Bar; public sealed record Widget;"),
            ("B.cs", "namespace Foo.Baz; public sealed record Widget;"),
        };

        var namespaces = NamespacesOf(FindDeclaringFilesByName(synthetic, ["Widget"])["Widget"]);

        namespaces.Should().BeEquivalentTo(["Foo.Bar", "Foo.Baz"],
            "two genuinely different production types sharing a bare name must both surface as "
            + "distinct namespaces, or the #528 collision guard could never detect the hazard it "
            + "exists to catch");
    }

    /// <summary>
    /// Negative control for <see cref="ConsumerResolvedValidatedTypes_HaveNoNamespaceCollision"/> —
    /// proves a type legitimately split across <c>partial</c> files in the SAME namespace is not
    /// itself reported as a false collision.
    /// </summary>
    [Fact]
    public void FindDeclaringFilesByName_PartialAcrossFilesInTheSameNamespace_OneNamespaceOnly()
    {
        var synthetic = new (string Path, string Code)[]
        {
            ("A.cs", "namespace Foo.Bar; public sealed partial record Widget;"),
            ("B.cs", "namespace Foo.Bar; public sealed partial record Widget;"),
        };

        var namespaces = NamespacesOf(FindDeclaringFilesByName(synthetic, ["Widget"])["Widget"]);

        namespaces.Should().ContainSingle(
                "a type legitimately split across partial declarations in the SAME namespace — this "
                + "repo's own documented Partial Class Pattern — must not be reported as a false "
                + "collision")
            .Which.Should().Be("Foo.Bar");
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
    /// only with a filed issue and a measured reason the defect is not urgent — see
    /// <see cref="ConsumerResolvedValidatedTypes"/>'s remarks for what happened the first time this
    /// file's own guard could not see the egress manifest pair's shape. Currently empty: #531's fix
    /// wired the pair into <c>SkillMetadataParser</c>, so they moved to
    /// <see cref="SkillEgressConsumerResolvedTypes"/> below instead of staying parked here.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownDeadValidators = [];

    /// <summary>
    /// Every entry on <see cref="KnownDeadValidators"/> must still be dead, so the exemption cannot
    /// outlive the defect it documents.
    /// </summary>
    /// <remarks>
    /// Fails in the direction that matters. The moment someone wires one of these — which is the fix
    /// #531 asks for — this test reports that the exemption is now false and must be removed, rather
    /// than letting a newly-live validator sit permanently outside the guard's scope. A dead-control
    /// exemption that survives the control coming alive is how the scope of a guard quietly shrinks.
    /// <para>
    /// Checks two caller shapes, not one — a mutation-test run against #531's own fix (deliberately
    /// re-adding "EgressManifestValidator" here while its real DI-resolved caller in
    /// <c>SkillMetadataParser</c> stood) found the bare-name check alone gives a false "still dead":
    /// a consumer that resolves <c>IValidator&lt;TValidated&gt;</c> via constructor injection —
    /// exactly how #531 wires <c>SkillMetadataParser</c> — never spells the concrete validator class
    /// name anywhere, so a name-only scan is blind to it. The second check closes that gap by looking
    /// for the validated TYPE inside an <c>IValidator&lt;&gt;</c> mention instead.
    /// </para>
    /// </remarks>
    [Fact]
    public void KnownDeadValidators_AreStillDead()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");

        var production = SourceScan.ReadProductionSources(contentRoot);

        production.Should().NotBeEmpty("the production source this reads must exist for its verdict to mean anything");

        var revived = new List<string>();
        var declaringFilesByValidator = FindDeclaringFilesByName(production, KnownDeadValidators);

        foreach (var validator in KnownDeadValidators)
        {
            // A caller is any production mention outside the file that declares it. The declaring file
            // is excluded because a parent validator legitimately names its child via SetValidator,
            // which is self-reference, not a consumer.
            var declaringFiles = declaringFilesByValidator[validator].ToArray();

            declaringFiles.Select(f => f.Path).Should().ContainSingle(
                $"{validator} must still be declared exactly once for this exemption to describe anything real");

            var declaringPath = declaringFiles[0].Path;
            var validated = FindValidatorDeclarations(declaringFiles[0].Code)
                .FirstOrDefault(d => d.Validator == validator).Validated;

            var callers = production
                .Where(f => !string.Equals(f.Path, declaringPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(f.Code, $@"\b{validator}\b")
                    // A DI-resolved caller (constructor injection of IValidator<TValidated>) never
                    // spells the concrete class name — see the remarks above.
                    || (validated is not null && Regex.IsMatch(f.Code, $@"\bIValidator\s*<\s*{validated}\s*>")))
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
}
