namespace Domain.AI.Sandbox;

/// <summary>
/// Declares the capabilities and minimum isolation level required by a tool class
/// at compile time. Applied to tool implementations. Can be overridden at runtime
/// via appsettings configuration (section-16).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolCapabilityAttribute : Attribute
{
    /// <summary>The capabilities this tool requires.</summary>
    public ToolCapability Capabilities { get; }

    /// <summary>
    /// Minimum sandbox isolation level. Defaults to <see cref="SandboxIsolationLevel.Process"/>.
    /// </summary>
    public SandboxIsolationLevel MinimumIsolation { get; init; } = SandboxIsolationLevel.Process;

    /// <summary>
    /// Initializes a new <see cref="ToolCapabilityAttribute"/> with the specified capabilities.
    /// </summary>
    /// <param name="capabilities">Required capabilities for the tool.</param>
    public ToolCapabilityAttribute(ToolCapability capabilities)
    {
        Capabilities = capabilities;
    }
}
