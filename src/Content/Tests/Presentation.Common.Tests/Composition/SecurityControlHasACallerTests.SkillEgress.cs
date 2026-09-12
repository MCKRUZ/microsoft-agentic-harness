using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

public sealed partial class SecurityControlHasACallerTests
{
    /// <summary>
    /// #531: <c>SkillMetadataParser</c> resolves <c>IValidator&lt;EgressManifest&gt;</c> via
    /// constructor injection and calls <c>.Validate()</c> on it directly — a genuine consumer-resolved
    /// mechanism, but a different shape from <see cref="ConsumerResolvedValidatedTypes"/>'s (which is
    /// anchored specifically to <c>PlanValidator</c>'s generic dispatch-arm pattern and would never
    /// match a direct field-call site). Given its own list and its own staleness check rather than
    /// folded into that one, for the same reason <see cref="KnownDeadValidators"/> is its own list:
    /// two different mechanisms proven two different ways must not collapse into one, or a future
    /// reader cannot tell which proof backs which entry.
    /// </summary>
    private static readonly string[] SkillEgressConsumerResolvedTypes =
    [
        // EgressManifestValidator's validated type — proven invoked below by finding the actual
        // .Validate() call in SkillMetadataParser.cs.
        "EgressManifest",
        // EgressAllowlistEntryValidator's validated type. Never resolved directly — EgressManifestValidator
        // composes it as a child via RuleForEach(...).SetValidator(...), which FluentValidation
        // runs unconditionally as part of the parent's own Validate() call. Proven below by finding
        // that composition, not by a separate top-level call site (there is none).
        "EgressAllowlistEntry"
    ];

    /// <summary>
    /// Both entries on <see cref="SkillEgressConsumerResolvedTypes"/> must still be genuinely invoked,
    /// so the exemption cannot outlive the wiring #531 put in place.
    /// </summary>
    /// <remarks>
    /// Two independent proofs, matching the two different invocation shapes the list documents: a
    /// direct <c>.Validate()</c> call site for the parent, and a <c>SetValidator</c> composition for
    /// the child. Either regressing independently reopens the exact gap #531 closed for that half.
    /// </remarks>
    [Fact]
    public void SkillEgressConsumerResolvedExemptions_AreStillInvoked()
    {
        // SkillMetadataParser.Egress.cs, not SkillMetadataParser.cs: ValidateEgressOrRefuse (and the
        // .Validate() call this control anchors on) lives in that partial-class file, split out to
        // keep the main file under this repo's 400-line convention.
        var parserPath = Path.Combine(
            RepoRoot.Path, "src", "Content", "Infrastructure", "Infrastructure.AI", "Skills", "SkillMetadataParser.Egress.cs");
        File.Exists(parserPath).Should().BeTrue("the consumer that justifies the EgressManifest exemption must exist");

        var parserSource = SourceScan.StripCommentsAndStrings(File.ReadAllText(parserPath));
        Regex.IsMatch(parserSource, @"_egressValidator\.Validate\(").Should().BeTrue(
            "control: SkillMetadataParser must actually call .Validate() on the injected "
            + "IValidator<EgressManifest> for the EgressManifest exemption to describe anything real");

        var validatorPath = Path.Combine(
            RepoRoot.Path, "src", "Content", "Application", "Application.AI.Common", "Skills", "EgressManifestValidator.cs");
        File.Exists(validatorPath).Should().BeTrue("the parent validator that composes the child must exist");

        var validatorSource = SourceScan.StripCommentsAndStrings(File.ReadAllText(validatorPath));
        Regex.IsMatch(validatorSource, @"SetValidator\(new EgressAllowlistEntryValidator\(\)\)").Should().BeTrue(
            "control: EgressManifestValidator must still compose EgressAllowlistEntryValidator as a "
            + "child, or the EgressAllowlistEntry exemption is stale and the validator is unreachable");
    }
}
