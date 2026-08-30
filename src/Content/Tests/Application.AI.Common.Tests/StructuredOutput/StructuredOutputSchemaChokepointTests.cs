using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Application.AI.Common.Tests.StructuredOutput;

/// <summary>
/// Asserts that <c>StructuredOutputSchema</c> is the <strong>only</strong> caller of
/// <c>AIJsonUtilities.CreateJsonSchema</c> — the same source-scan shape as
/// <c>ToolCallAdmissionChokepointTests</c>, for the same reason: the defect this guards against is a
/// new call site choosing its own schema-generation posture (required/nullable/additionalProperties)
/// independently, which is exactly how a schema can silently reject valid model output — not a wrong
/// answer from the one component that has it right.
/// </summary>
public sealed class StructuredOutputSchemaChokepointTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "StructuredOutputSchema.cs",
    };

    [Fact]
    public void StructuredOutputSchema_IsTheOnlyCallerOfCreateJsonSchema()
    {
        var contentRoot = Path.Combine(RepoRoot.Path, "src", "Content");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(contentRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(file, contentRoot) || Allowed.Contains(Path.GetFileName(file)))
                continue;

            var code = SourceScan.StripCommentsAndStrings(File.ReadAllText(file));
            if (Regex.IsMatch(code, @"\bAIJsonUtilities\.CreateJsonSchema\b"))
                offenders.Add(Path.GetRelativePath(contentRoot, file));
        }

        offenders.Should().BeEmpty(
            "every structured-output schema must be generated under StructuredOutputSchema's fixed "
            + "posture — a second call site choosing its own required/nullable/additionalProperties "
            + "settings is how a schema silently starts rejecting valid model output. Offenders:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheGuardWouldActuallyFire()
    {
        var violating = SourceScan.StripCommentsAndStrings(
            "var schema = AIJsonUtilities.CreateJsonSchema(typeof(Foo), null, false, null, null, null);");
        var commentOnly = SourceScan.StripCommentsAndStrings(
            "// see AIJsonUtilities.CreateJsonSchema for the underlying call\npublic class X { }");

        Regex.IsMatch(violating, @"\bAIJsonUtilities\.CreateJsonSchema\b").Should().BeTrue();
        Regex.IsMatch(commentOnly, @"\bAIJsonUtilities\.CreateJsonSchema\b").Should().BeFalse();
    }
}
