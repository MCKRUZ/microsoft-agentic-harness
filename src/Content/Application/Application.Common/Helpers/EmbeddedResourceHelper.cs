using System.Reflection;

namespace Application.Common.Helpers;

/// <summary>
/// Reads embedded resources from assemblies by name. Used for loading AGENT.md,
/// SKILL.md, bundled skill content, tool definition schemas, and prompt templates
/// compiled into assemblies.
/// </summary>
/// <remarks>
/// Resource names follow the convention: <c>Namespace.Folder.Filename.ext</c>.
/// Use <c>Assembly.GetManifestResourceNames()</c> to discover available resources.
/// </remarks>
public static class EmbeddedResourceHelper
{
    /// <summary>
    /// Reads an embedded resource as a UTF-8 string.
    /// </summary>
    /// <param name="resourceName">The fully-qualified resource name.</param>
    /// <param name="assemblyName">The assembly containing the resource.</param>
    /// <returns>The resource content as a string.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the assembly cannot be loaded or the resource is not found.
    /// </exception>
    /// <example>
    /// <code>
    /// var manifest = EmbeddedResourceHelper.ReadAsString(
    ///     "MyProject.Skills.CodeReview.SKILL.md",
    ///     "MyProject.Skills");
    /// </code>
    /// </example>
    public static string ReadAsString(string resourceName, string assemblyName)
    {
        using var stream = GetResourceStream(resourceName, assemblyName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads an embedded resource as a byte array.
    /// </summary>
    /// <param name="resourceName">The fully-qualified resource name.</param>
    /// <param name="assemblyName">The assembly containing the resource.</param>
    /// <returns>The resource content as a byte array.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the assembly cannot be loaded or the resource is not found.
    /// </exception>
    public static byte[] ReadAsBytes(string resourceName, string assemblyName)
    {
        using var stream = GetResourceStream(resourceName, assemblyName);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Opens an embedded resource as a readable stream. The caller is responsible
    /// for disposing the returned stream.
    /// </summary>
    /// <param name="resourceName">The fully-qualified resource name.</param>
    /// <param name="assemblyName">The assembly containing the resource.</param>
    /// <returns>A readable <see cref="Stream"/> for the resource.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the assembly cannot be loaded or the resource is not found.
    /// </exception>
    public static Stream OpenStream(string resourceName, string assemblyName)
    {
        return GetResourceStream(resourceName, assemblyName);
    }

    private static Stream GetResourceStream(string resourceName, string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        var assembly = Assembly.Load(assemblyName)
            ?? throw new InvalidOperationException($"Assembly '{assemblyName}' could not be loaded.");

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Resource '{resourceName}' not found in assembly '{assemblyName}'. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
    }
}
