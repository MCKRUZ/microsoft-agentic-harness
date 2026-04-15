diff --git a/src/Content/Application/Application.AI.Common/Interfaces/ISecretRedactor.cs b/src/Content/Application/Application.AI.Common/Interfaces/ISecretRedactor.cs
new file mode 100644
index 0000000..ea7492c
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/ISecretRedactor.cs
@@ -0,0 +1,50 @@
+namespace Application.AI.Common.Interfaces;
+
+/// <summary>
+/// Redacts secrets from free-text strings and filters secret config keys.
+/// Applied before any content is persisted to disk (traces, snapshots, manifests).
+/// </summary>
+/// <remarks>
+/// <para>
+/// The redactor operates at two boundaries:
+/// <list type="number">
+/// <item><description>
+/// <strong>Config key filtering</strong> — use <see cref="IsSecretKey"/> when building
+/// <c>HarnessSnapshot</c> to exclude keys whose names contain a denylist pattern.
+/// </description></item>
+/// <item><description>
+/// <strong>Free-text redaction</strong> — use <see cref="Redact"/> when writing any
+/// string artifact to disk to replace recognizable secret shapes with <c>"[REDACTED]"</c>.
+/// </description></item>
+/// </list>
+/// </para>
+/// <para>
+/// Implementations must be thread-safe and idempotent: applying <see cref="Redact"/>
+/// twice to the same input must produce the same output as applying it once.
+/// </para>
+/// </remarks>
+public interface ISecretRedactor
+{
+    /// <summary>
+    /// Scans <paramref name="input"/> for known secret patterns and replaces
+    /// matches with <c>"[REDACTED]"</c>. Returns the original string if no patterns match.
+    /// Returns <see langword="null"/> or empty unchanged.
+    /// </summary>
+    /// <param name="input">The string to scan. May be null or empty.</param>
+    /// <returns>
+    /// The redacted string if any patterns matched; the original <paramref name="input"/>
+    /// reference if no patterns matched (no allocation).
+    /// </returns>
+    string? Redact(string? input);
+
+    /// <summary>
+    /// Returns <see langword="true"/> if <paramref name="configKey"/> matches any entry in the
+    /// secrets denylist and should therefore be excluded from config snapshots.
+    /// Matching is case-insensitive substring comparison.
+    /// </summary>
+    /// <param name="configKey">The configuration key name to evaluate.</param>
+    /// <returns>
+    /// <see langword="true"/> if the key contains a denylist pattern; otherwise <see langword="false"/>.
+    /// </returns>
+    bool IsSecretKey(string configKey);
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index dfa767c..4ac0ef9 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,5 +1,6 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Infrastructure.AI.Security;
 using Application.AI.Common.Interfaces.Agent;
 using Application.AI.Common.Interfaces.Agents;
 using Application.AI.Common.Interfaces.Compaction;
@@ -65,6 +66,9 @@ public static class DependencyInjection
         this IServiceCollection services,
         AppConfig appConfig)
     {
+        // Secret redaction — applied at all persistence boundaries (traces, snapshots, manifests)
+        services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();
+
         // AI client registration — AzureOpenAIClient or OpenAIClient based on config
         RegisterAIClients(services, appConfig);
 
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Security/PatternSecretRedactor.cs b/src/Content/Infrastructure/Infrastructure.AI/Security/PatternSecretRedactor.cs
new file mode 100644
index 0000000..ef662f9
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Security/PatternSecretRedactor.cs
@@ -0,0 +1,102 @@
+using System.Text.RegularExpressions;
+using Application.AI.Common.Interfaces;
+using Domain.Common.Config.MetaHarness;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Security;
+
+/// <summary>
+/// Regex-based secret redactor that scans free-text strings for known secret patterns
+/// and filters config keys by a configurable denylist.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Patterns are compiled once at construction time from <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
+/// and the hardcoded free-text regex set. Config changes are not reflected at runtime — restart
+/// the service to pick up updated denylist patterns.
+/// </para>
+/// <para>
+/// All methods are thread-safe: compiled <see cref="Regex"/> instances are stateless after
+/// construction, and the denylist is an immutable snapshot captured at startup.
+/// </para>
+/// </remarks>
+public sealed class PatternSecretRedactor : ISecretRedactor
+{
+    private readonly IReadOnlyList<string> _denylistPatterns;
+    private readonly (Regex Pattern, string Replacement)[] _redactionPatterns;
+
+    /// <summary>
+    /// Initializes a new instance using the meta-harness configuration for the denylist.
+    /// </summary>
+    /// <param name="config">
+    /// The meta-harness configuration monitor. Only <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/>
+    /// is read, and only at construction time. Changes to the config after startup are ignored.
+    /// </param>
+    public PatternSecretRedactor(IOptionsMonitor<MetaHarnessConfig> config)
+        : this(config.CurrentValue.SecretsRedactionPatterns)
+    {
+    }
+
+    /// <summary>
+    /// Initializes a new instance with an explicit denylist. Intended for testing.
+    /// </summary>
+    /// <param name="denylistPatterns">
+    /// Case-insensitive substrings matched against config key names by <see cref="IsSecretKey"/>.
+    /// </param>
+    internal PatternSecretRedactor(IReadOnlyList<string> denylistPatterns)
+    {
+        _denylistPatterns = denylistPatterns;
+        _redactionPatterns = BuildRedactionPatterns();
+    }
+
+    /// <inheritdoc />
+    public string? Redact(string? input)
+    {
+        if (string.IsNullOrEmpty(input))
+            return input;
+
+        var result = input;
+        foreach (var (pattern, replacement) in _redactionPatterns)
+            result = pattern.Replace(result, replacement);
+
+        return result;
+    }
+
+    /// <inheritdoc />
+    public bool IsSecretKey(string configKey)
+    {
+        foreach (var pattern in _denylistPatterns)
+        {
+            if (configKey.Contains(pattern, StringComparison.OrdinalIgnoreCase))
+                return true;
+        }
+        return false;
+    }
+
+    private static (Regex Pattern, string Replacement)[] BuildRedactionPatterns() =>
+    [
+        // Bearer tokens: replace entire match, preserving the "Bearer" prefix label
+        (
+            new Regex(
+                @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
+                RegexOptions.Compiled),
+            "Bearer [REDACTED]"
+        ),
+
+        // Connection string value segments: AccountKey=..., Password=..., etc.
+        (
+            new Regex(
+                @"(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*[^;""'\s]+",
+                RegexOptions.Compiled),
+            "$1=[REDACTED]"
+        ),
+
+        // Generic key=value / key:value secret pairs
+        (
+            new Regex(
+                @"(?i)(api[_-]?key|access[_-]?token|secret[_-]?key)\s*[=:]\s*\S+",
+                RegexOptions.Compiled),
+            "$1=[REDACTED]"
+        ),
+    ];
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Security/PatternSecretRedactorTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Security/PatternSecretRedactorTests.cs
new file mode 100644
index 0000000..aedf78f
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Security/PatternSecretRedactorTests.cs
@@ -0,0 +1,136 @@
+using Domain.Common.Config.MetaHarness;
+using FluentAssertions;
+using Infrastructure.AI.Security;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Security;
+
+public class PatternSecretRedactorTests
+{
+    private static PatternSecretRedactor CreateRedactor(
+        IReadOnlyList<string>? denylist = null)
+    {
+        var config = new MetaHarnessConfig
+        {
+            SecretsRedactionPatterns = denylist
+                ?? ["Key", "Secret", "Token", "Password", "ConnectionString"]
+        };
+        var monitor = new Mock<IOptionsMonitor<MetaHarnessConfig>>();
+        monitor.Setup(m => m.CurrentValue).Returns(config);
+        return new PatternSecretRedactor(monitor.Object);
+    }
+
+    /// <summary>
+    /// A string containing "Authorization: Bearer eyABC123..." has the token value
+    /// replaced with "[REDACTED]", leaving the "Bearer" prefix intact.
+    /// </summary>
+    [Fact]
+    public void Redact_StringContainingBearerToken_ReplacesWithRedacted()
+    {
+        var sut = CreateRedactor();
+        var input = "Authorization: Bearer eyABC123xyz==";
+
+        var result = sut.Redact(input);
+
+        result.Should().Be("Authorization: Bearer [REDACTED]");
+    }
+
+    /// <summary>
+    /// A plain string with no secret patterns is returned exactly as-is
+    /// (same reference or equal value, no mutation).
+    /// </summary>
+    [Fact]
+    public void Redact_StringWithNoSecrets_ReturnsUnchanged()
+    {
+        var sut = CreateRedactor();
+        var input = "The quick brown fox jumped over the lazy dog.";
+
+        var result = sut.Redact(input);
+
+        result.Should().Be(input);
+    }
+
+    /// <summary>
+    /// A config key named "AzureOpenAIApiKey" matches the "Key" pattern and
+    /// IsSecretKey returns true.
+    /// </summary>
+    [Fact]
+    public void IsSecretKey_KeyMatchingDenylistPattern_ReturnsTrue()
+    {
+        var sut = CreateRedactor();
+
+        sut.IsSecretKey("AzureOpenAIApiKey").Should().BeTrue();
+    }
+
+    /// <summary>
+    /// A config key named "MaxIterations" does not match any denylist pattern
+    /// and IsSecretKey returns false.
+    /// </summary>
+    [Fact]
+    public void IsSecretKey_KeyNotMatchingAnyPattern_ReturnsFalse()
+    {
+        var sut = CreateRedactor();
+
+        sut.IsSecretKey("MaxIterations").Should().BeFalse();
+    }
+
+    /// <summary>
+    /// IsSecretKey matching is case-insensitive: "apikey" matches "Key".
+    /// </summary>
+    [Fact]
+    public void IsSecretKey_CaseInsensitiveMatch_ReturnsTrue()
+    {
+        var sut = CreateRedactor();
+
+        sut.IsSecretKey("apikey").Should().BeTrue();
+    }
+
+    /// <summary>
+    /// Redact(null) returns null without throwing.
+    /// Redact("") returns "" without throwing.
+    /// </summary>
+    [Fact]
+    public void Redact_NullOrEmpty_ReturnsInputUnchanged()
+    {
+        var sut = CreateRedactor();
+
+        sut.Redact(null).Should().BeNull();
+        sut.Redact("").Should().Be("");
+    }
+
+    /// <summary>
+    /// A connection string containing "AccountKey=abc123;" has the value portion
+    /// replaced with "[REDACTED]".
+    /// </summary>
+    [Fact]
+    public void Redact_ConnectionStringWithAccountKey_RedactsValue()
+    {
+        var sut = CreateRedactor();
+        var input = "DefaultEndpointsProtocol=https;AccountKey=abc123secret;EndpointSuffix=core.windows.net";
+
+        var result = sut.Redact(input);
+
+        result.Should().Contain("AccountKey=[REDACTED]");
+        result.Should().NotContain("abc123secret");
+    }
+
+    /// <summary>
+    /// A string with multiple secret occurrences has all of them redacted,
+    /// not just the first match.
+    /// </summary>
+    [Fact]
+    public void Redact_MultipleSecretsInInput_RedactsAll()
+    {
+        var sut = CreateRedactor();
+        var input = "Bearer tokenABC and api_key=superSecret123";
+
+        var result = sut.Redact(input);
+
+        result.Should().Contain("Bearer [REDACTED]");
+        result.Should().Contain("[REDACTED]");
+        result.Should().NotContain("tokenABC");
+        result.Should().NotContain("superSecret123");
+    }
+}
