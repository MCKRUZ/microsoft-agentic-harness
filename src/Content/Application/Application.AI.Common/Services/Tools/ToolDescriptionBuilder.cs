using System.Text;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Fluent builder for constructing consistent AI tool descriptions that include
/// purpose, supported operations, and parameter documentation.
/// </summary>
/// <remarks>
/// Produces descriptions consumed by LLMs to understand tool capabilities.
/// The format is optimized for token efficiency while remaining human-readable.
/// </remarks>
public sealed class ToolDescriptionBuilder
{
    private readonly StringBuilder _sb = new();

    /// <summary>Adds the tool's purpose/main description.</summary>
    public ToolDescriptionBuilder AddPurpose(string purpose)
    {
        if (!string.IsNullOrWhiteSpace(purpose))
            _sb.Append(purpose);
        return this;
    }

    /// <summary>Adds a section header with content.</summary>
    public ToolDescriptionBuilder AddSection(string header, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            EnsureNewLine();
            _sb.Append(header).Append(": ").Append(content);
        }
        return this;
    }

    /// <summary>Adds a list of supported operations.</summary>
    public ToolDescriptionBuilder AddOperations(IEnumerable<string> operations)
    {
        var ops = operations?.ToList();
        if (ops is { Count: > 0 })
        {
            EnsureNewLine();
            _sb.Append("Supported operations: ").Append(string.Join(", ", ops));
        }
        return this;
    }

    /// <summary>Adds a parameter with its requirement status and optional description.</summary>
    public ToolDescriptionBuilder AddParameter(string name, bool required, string? description = null)
    {
        EnsureNewLine();
        _sb.Append("- ").Append(name);
        _sb.Append(required ? " (required)" : " (optional)");
        if (!string.IsNullOrWhiteSpace(description))
            _sb.Append(": ").Append(description);
        return this;
    }

    /// <summary>Adds multiple parameters with descriptions.</summary>
    public ToolDescriptionBuilder AddParameters(params (string Name, bool Required, string? Description)[] parameters)
    {
        foreach (var (name, required, description) in parameters)
            AddParameter(name, required, description);
        return this;
    }

    /// <summary>Adds a note or additional information.</summary>
    public ToolDescriptionBuilder AddNote(string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            EnsureNewLine();
            _sb.Append("Note: ").Append(note);
        }
        return this;
    }

    /// <summary>Builds the final description string.</summary>
    public string Build() => _sb.ToString();

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(ToolDescriptionBuilder builder) => builder.Build();

    private void EnsureNewLine()
    {
        if (_sb.Length > 0)
            _sb.AppendLine();
    }
}
