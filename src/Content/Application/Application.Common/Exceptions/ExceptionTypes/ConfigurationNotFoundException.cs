using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when required configuration values cannot be found
/// in the application configuration sources (appsettings.json, environment variables, etc.).
/// </summary>
/// <remarks>
/// This exception provides structured context about which configuration section and/or key
/// is missing, making it easier to diagnose configuration issues. Throw this exception early
/// in the application lifecycle to prevent runtime failures. Common scenarios include:
/// <list type="bullet">
///   <item><description>Missing connection strings in appsettings.json</description></item>
///   <item><description>Required environment variables not set</description></item>
///   <item><description>Configuration sections not properly registered</description></item>
///   <item><description>Invalid configuration hierarchy or structure</description></item>
///   <item><description>Configuration provider failures</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var connectionString = configuration["Database:ConnectionString"]
///     ?? throw new ConfigurationNotFoundException("Database", "ConnectionString");
/// </code>
/// </example>
public sealed class ConfigurationNotFoundException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the configuration section that was not found, if specified.
    /// </summary>
    /// <value>
    /// The section name (e.g., "Database", "AI:AgentFramework"), or <c>null</c> if not provided.
    /// </value>
    public string? Section { get; init; }

    /// <summary>
    /// Gets the configuration key that was not found, if specified.
    /// </summary>
    /// <value>
    /// The specific key within the section (e.g., "ConnectionString", "Endpoint"),
    /// or <c>null</c> if not provided.
    /// </value>
    public string? Key { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationNotFoundException"/> class
    /// with a default error message.
    /// </summary>
    public ConfigurationNotFoundException()
        : base("A required configuration value was not found.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationNotFoundException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the missing configuration.</param>
    public ConfigurationNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationNotFoundException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the missing configuration.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    /// <example>
    /// <code>
    /// catch (IOException ex)
    /// {
    ///     throw new ConfigurationNotFoundException("Failed to read configuration file.", ex);
    /// }
    /// </code>
    /// </example>
    public ConfigurationNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationNotFoundException"/> class
    /// with a formatted message describing the missing configuration section.
    /// </summary>
    /// <param name="section">The configuration section that was not found.</param>
    /// <param name="key">The specific key within the section that was not found.</param>
    /// <example>
    /// <code>
    /// throw new ConfigurationNotFoundException("Database", "ConnectionString");
    /// // Message: "Configuration section 'Database' with key 'ConnectionString' was not found."
    /// </code>
    /// </example>
    public ConfigurationNotFoundException(string section, string key)
        : base($"Configuration section '{section}' with key '{key}' was not found.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Section = section;
        Key = key;
    }

    /// <summary>
    /// Creates a <see cref="ConfigurationNotFoundException"/> for a missing configuration section.
    /// </summary>
    /// <param name="section">The configuration section that was not found.</param>
    /// <returns>A new <see cref="ConfigurationNotFoundException"/> with the <see cref="Section"/> property set.</returns>
    /// <example>
    /// <code>
    /// if (!configuration.GetSection("AI").Exists())
    /// {
    ///     throw ConfigurationNotFoundException.ForSection("AI");
    /// }
    /// </code>
    /// </example>
    public static ConfigurationNotFoundException ForSection(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return new ConfigurationNotFoundException($"Configuration section '{section}' was not found.")
        {
            Section = section
        };
    }
}
