using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Infrastructure.AI.Governance.Adapters;

/// <summary>
/// Guards against a policy YAML's <c>default_action</c> field being silently ignored because it was
/// written in a different casing.
/// </summary>
/// <remarks>
/// Microsoft.AgentGovernance's <c>Policy.FromYaml</c> deserializes with YamlDotNet's
/// <c>UnderscoredNamingConvention</c> and no per-field override for <c>DefaultAction</c> — unlike
/// <c>ApiVersion</c>, which carries an explicit <c>[YamlMember(Alias = "apiVersion")]</c>. Combined with
/// <c>IgnoreUnmatchedProperties()</c>, a policy author who writes the natural-looking camelCase
/// <c>defaultAction</c> gets it silently dropped: no error, no warning, and a policy that looks correct
/// but falls back to denying every tool that doesn't match an explicit rule. This shipped in the
/// harness's own <c>default-policy.yaml</c> once (#384).
/// <para>
/// <b>Two call sites, both required, both through <see cref="ReadAndValidate"/>.</b> A policy file
/// reaches the AGT engine two ways: dynamically via <see cref="AgtPolicyEngineAdapter.LoadPolicyFile"/>,
/// and at startup via <c>DependencyInjection.ReadAndValidatePolicyFiles</c>, whose content then feeds
/// <c>GovernanceKernel.LoadPolicyFromYaml</c> rather than the kernel's own constructor, which would
/// otherwise load every configured <c>PolicyPaths</c> entry itself (<c>PolicyEngine.LoadYamlFile</c> in
/// a loop) and bypass this guard entirely. The startup path is the one that loaded the actual miscased
/// file in #384, so this guard must run there too, not only on the adapter's method.
/// </para>
/// <para>
/// The check is structural (parses the document's real top-level keys) rather than a text/regex scan,
/// so it isn't fooled by indentation, quoting, casing variants beyond the one exact mistake, or a
/// coincidental substring inside an unrelated string value.
/// </para>
/// <para>
/// <b>Considered and declined: a fully general strict deserializer.</b>
/// <c>PolicyEngine.LoadPolicy(Policy)</c> is public, and nothing stops the harness building its own
/// <c>Policy</c>/<c>PolicyRule</c> graph via a deserializer with <c>IgnoreUnmatchedProperties()</c> off,
/// catching any unrecognized key generically instead of hand-checking one. Declined for now: that means
/// re-implementing AGT's own YAML-to-<c>Policy</c> mapping ourselves, with real risk of silently
/// diverging from AGT's actual parsing semantics as it evolves, to generalize a fix for a vulnerability
/// surface that is currently exactly one field wide (see <c>ApiVersion</c>'s alias and every
/// <c>PolicyRule</c> property being single-word, noted above) — worth revisiting if that stops being true.
/// </para>
/// </remarks>
internal static class PolicyYamlGuard
{
    private const string CorrectKey = "default_action";

    /// <summary>
    /// Reads <paramref name="yamlPath"/> once and validates it, so a caller that also needs the content
    /// (to hand to <c>PolicyEngine.LoadYaml</c> instead of re-reading the same file) gets both from one
    /// I/O call.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist. Matches the message shape <c>Policy.FromYamlFile</c> itself throws, so
    /// this check does not change the caller-visible contract for a missing file.
    /// </exception>
    /// <exception cref="InvalidOperationException">The document uses a mis-cased default-action key.</exception>
    public static string ReadAndValidate(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Policy file not found: '{yamlPath}'", yamlPath);

        var content = File.ReadAllText(yamlPath);
        Validate(yamlPath, content);
        return content;
    }

    // Not exposed publicly: every real load path goes through ReadAndValidate above, which reads the
    // file itself before calling this. Kept as a separate method for readability, not as a second
    // supported entry point.
    // No naming convention applied — this deliberately reads keys exactly as the author wrote them,
    // which is the whole point: checking what they wrote, not what AGT's own (different) convention
    // would normalize it to. WithMaximumRecursion matches the bound YamlEvalDatasetLoader already
    // applies to YAML read off disk elsewhere in this repo — defense-in-depth against a pathologically
    // deep document, not something this guard's own logic needs.
    private static readonly IDeserializer StructuralDeserializer =
        new DeserializerBuilder().WithMaximumRecursion(64).Build();

    private static void Validate(string yamlPath, string content)
    {
        Dictionary<object, object>? topLevel;
        try
        {
            topLevel = StructuralDeserializer.Deserialize<Dictionary<object, object>>(content);
        }
        catch (YamlException)
        {
            // Malformed YAML is the underlying engine's error to raise, in its own shape — this guard
            // exists to catch one specific otherwise-silent mistake, not to be a general YAML validator.
            // Safe only because Dictionary<object, object> accepts a strict superset of what AGT's own
            // YamlPolicyDocument shape accepts (same parser, looser target) — anything that fails to
            // deserialize here would fail in AGT's own parse too, so skipping the check here doesn't let
            // a genuinely malformed document slip through unnoticed.
            return;
        }

        if (topLevel is null)
            return;

        var keys = topLevel.Keys.Select(k => k?.ToString() ?? string.Empty).ToList();
        if (keys.Contains(CorrectKey))
            return;

        var misCased = keys.FirstOrDefault(k => Normalize(k).Equals("defaultaction", StringComparison.OrdinalIgnoreCase));

        if (misCased is not null)
            throw new InvalidOperationException(
                $"Policy YAML '{yamlPath}' declares '{misCased}', but the governance engine's YAML " +
                $"parser only recognizes the exact key '{CorrectKey}' (snake_case) — any other casing " +
                "is silently ignored, and the policy falls back to denying every tool that doesn't " +
                $"match an explicit rule. Rename '{misCased}' to '{CorrectKey}'.");
    }

    // Strips every separator style a policy author might reach for instead of an underscore
    // (defaultaction, default-action, "default action") so all of them normalize to the same
    // comparison target, not just the underscore case.
    private static string Normalize(string key) =>
        new(key.Where(c => c is not ('_' or '-' or ' ')).ToArray());
}
