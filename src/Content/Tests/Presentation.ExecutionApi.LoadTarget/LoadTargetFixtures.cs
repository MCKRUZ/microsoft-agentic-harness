using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// Writes the files the k6 scenarios read at start-up: one token per simulated caller, a minimal
/// valid bundle archive, and a description of the running target.
/// </summary>
/// <remarks>
/// k6 cannot sign tokens or build zip archives, and should not need to: both are prepared once, here,
/// by the same process that configured the host to trust them, so the two can never drift apart.
/// </remarks>
public static class LoadTargetFixtures
{
    /// <summary>The app role the tool-invocation endpoint requires.</summary>
    public const string ToolInvokeRole = "Harness.Tools.Invoke";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Writes every fixture into <see cref="LoadTargetOptions.OutputDirectory"/>.</summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="issuer">The issuer the host was configured to trust.</param>
    /// <param name="baseUrl">The address the host is listening on.</param>
    public static void Write(LoadTargetOptions options, LocalTokenIssuer issuer, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(baseUrl);

        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "tokens.json"),
            JsonSerializer.Serialize(MintCallers(options, issuer), Json));
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, "bundle.zip"), BuildBundleZip());
        File.WriteAllText(Path.Combine(options.OutputDirectory, "target.json"), JsonSerializer.Serialize(new
        {
            baseUrl = baseUrl.ToString().TrimEnd('/'),
            model = options.Model,
            options.Callers,
            options.Tenants,
            workflowAgent = LoadTargetEnvironment.WorkflowAgent,
            toolInvocationEnabled = options.EnableToolInvocation,
            telemetryEnabled = options.WithTelemetry,
        }, Json));
    }

    private static IReadOnlyList<object> MintCallers(LoadTargetOptions options, LocalTokenIssuer issuer)
    {
        IReadOnlyList<string> roles = options.EnableToolInvocation ? [ToolInvokeRole] : [];
        return Enumerable.Range(0, options.Callers)
            .Select(i =>
            {
                var user = $"load-user-{i:D5}";
                var tenant = $"load-tenant-{i % options.Tenants:D3}";
                return (object)new { user, tenant, token = issuer.Mint(user, tenant, options.TokenLifetime, roles) };
            })
            .ToList();
    }

    /// <summary>
    /// The smallest bundle that runs a real agent turn: an <c>AGENT.md</c> plus the one nested skill it
    /// declares, the same shape as onboarding chapter 17's quickstart bundle. An <c>AGENT.md</c> alone
    /// registers but fails its first turn, because the run resolves the agent's skills. Kept tiny on
    /// purpose, so register scenarios measure staging overhead rather than upload size.
    /// </summary>
    private static byte[] BuildBundleZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "AGENT.md",
                "---\nid: load-agent\nname: Load Agent\ndescription: Load-test agent.\nversion: 1.0.0\n" +
                "skills: [\"answerer\"]\n---\n\nAnswer the user briefly.");
            AddEntry(archive, "skills/answerer/SKILL.md",
                "---\nname: \"answerer\"\ndescription: \"Answers short questions.\"\nversion: \"1.0.0\"\n---\n\n" +
                "Answer the question directly.");
        }

        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
