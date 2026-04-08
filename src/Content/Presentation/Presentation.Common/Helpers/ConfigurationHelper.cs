using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Presentation.Common.Helpers;

/// <summary>
/// Provides helper methods for Kestrel URL resolution and cross-project
/// configuration propagation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kestrel URL Resolution</strong> reads the <c>Kestrel:Endpoints</c> section
/// and selects HTTP (Development) or HTTPS (Production) endpoints with sensible defaults.
/// Wildcard bindings (<c>*</c>) can be replaced with <c>localhost</c> for security.
/// </para>
/// <para>
/// <strong>Config Propagation</strong> synchronizes the <c>appConfig</c> JSON node
/// from a source <c>appsettings.json</c> to one or more target files, ensuring
/// consistent configuration across multiple deployable projects.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Resolve Kestrel URL in Program.cs
/// var url = ConfigurationHelper.GetKestrelUrl(app.Configuration, app.Environment);
///
/// // Propagate config to satellite projects
/// ConfigurationHelper.PropagateAppConfigChanges(
///     "src/Presentation.ConsoleUI/appsettings.json",
///     new[] { "src/Infrastructure.AI.MCPServer/appsettings.json" });
/// </code>
/// </example>
public static class ConfigurationHelper
{
    /// <summary>
    /// Retrieves the configured Kestrel server URL based on environment and configuration settings.
    /// </summary>
    /// <param name="configuration">
    /// The application configuration containing <c>Kestrel:Endpoints</c> settings.
    /// </param>
    /// <param name="environment">
    /// The web host environment used to select HTTP (Development) or HTTPS (Production).
    /// </param>
    /// <param name="enforceLocalHost">
    /// When <c>true</c>, replaces wildcard (<c>*</c>) bindings with <c>localhost</c>
    /// to prevent binding to all network interfaces. Defaults to <c>true</c>.
    /// </param>
    /// <returns>
    /// The resolved server URL (e.g., <c>"http://localhost:8001/"</c>
    /// or <c>"https://localhost:8001/"</c>).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configuration"/> or <paramref name="environment"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Resolution strategy:
    /// <list type="numbered">
    ///   <item>Read <c>Kestrel:Endpoints:Http:Url</c> (Development) or <c>Kestrel:Endpoints:Https:Url</c> (Production)</item>
    ///   <item>Fall back to <c>http://localhost:8001/</c> or <c>https://localhost:8001/</c></item>
    ///   <item>Optionally replace <c>*</c> with <c>localhost</c> via regex</item>
    /// </list>
    /// </para>
    /// <para>
    /// Expected configuration structure:
    /// <code>
    /// {
    ///   "Kestrel": {
    ///     "Endpoints": {
    ///       "Http":  { "Url": "http://localhost:5000" },
    ///       "Https": { "Url": "https://*:5001" }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    public static string GetKestrelUrl(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        bool enforceLocalHost = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection("Kestrel:Endpoints");

        string url;
        if (environment.IsDevelopment())
            url = section.GetSection("Http")["Url"] ?? "http://localhost:8001/";
        else
            url = section.GetSection("Https")["Url"] ?? "https://localhost:8001/";

        if (enforceLocalHost)
        {
            url = Regex.Replace(
                url,
                @"^(https?://)\*(:\d+/?)",
                "$1localhost$2",
                RegexOptions.IgnoreCase);
        }

        return url;
    }

    /// <summary>
    /// Copies the <c>appConfig</c> JSON node from a source settings file into one or more
    /// target settings files, overwriting their existing <c>appConfig</c> sections.
    /// </summary>
    /// <param name="sourceFilePath">
    /// Path to the source <c>appsettings.json</c> containing the authoritative <c>appConfig</c> node.
    /// </param>
    /// <param name="targetFilePaths">
    /// Paths to the target <c>appsettings.json</c> files whose <c>appConfig</c> sections
    /// should be replaced.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source file does not contain an <c>appConfig</c> node.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Uses <see cref="JsonNode"/> (System.Text.Json) for read-modify-write without
    /// requiring strongly-typed deserialization. The target files are written with
    /// indented formatting for readability.
    /// </para>
    /// <para>
    /// The <c>appConfig</c> node is deep-cloned for each target to avoid shared
    /// mutable references between files.
    /// </para>
    /// </remarks>
    public static void PropagateAppConfigChanges(string sourceFilePath, string[] targetFilePaths)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePath);
        ArgumentNullException.ThrowIfNull(targetFilePaths);

        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Source settings file not found.", sourceFilePath);

        var sourceJson = JsonNode.Parse(File.ReadAllText(sourceFilePath))!.AsObject();
        var appConfigNode = sourceJson["appConfig"]
            ?? throw new InvalidOperationException(
                $"Source file '{sourceFilePath}' does not contain an 'appConfig' section.");

        foreach (var targetFilePath in targetFilePaths)
        {
            var targetJson = JsonNode.Parse(File.ReadAllText(targetFilePath))!.AsObject();
            targetJson["appConfig"] = appConfigNode.DeepClone();

            File.WriteAllText(
                targetFilePath,
                targetJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
