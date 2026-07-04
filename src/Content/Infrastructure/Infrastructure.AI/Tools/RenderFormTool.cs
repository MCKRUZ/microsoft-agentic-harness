using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Renders an interactive form inline in the agent's chat answer ("generative UI"): the agent supplies
/// a field spec, and the connected browser draws a real form. The browser acknowledges immediately that
/// the form was displayed; the user's answers are <em>not</em> returned through this tool. When the user
/// submits, the browser sends the collected values as an ordinary next user message, which the agent
/// handles as a normal turn.
/// </summary>
/// <remarks>
/// <para>
/// A <b>synchronous</b> client round-trip tool: like <see cref="RenderImageTool"/>, the browser replies
/// in milliseconds ("form displayed"), so the agent turn ends promptly rather than being held open while
/// a human fills the form. This is deliberate — the agent framework's turn (<c>AIAgent.RunAsync</c>)
/// owns an atomic tool loop and cannot be suspended to wait for human input, and holding the run open
/// would lose the form on a page refresh. Decoupling submission into a normal message survives refresh
/// and reuses the existing send path. See the plan's PR2 decision record.
/// </para>
/// <para>
/// The field <c>type</c> is validated against a fixed whitelist here so the browser only ever renders a
/// known control; the client registry validates again at the render boundary (defense in depth).
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;(RenderFormTool.ToolName, (sp, _) =&gt;
///     new RenderFormTool(sp.GetRequiredService&lt;IClientToolBridge&gt;()));
/// </code>
/// </para>
/// </remarks>
public sealed class RenderFormTool : SingleRenderProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "render_form";

    /// <summary>Field <c>type</c> values the browser knows how to render. Kept in sync with the client whitelist.</summary>
    private static readonly HashSet<string> AllowedFieldTypes =
        new(["text", "textarea", "number", "select", "checkbox", "date"], StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RenderFormTool"/> class.</summary>
    /// <param name="bridge">The client round-trip bridge used to delegate rendering to the browser.</param>
    public RenderFormTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Displays an interactive form inline in your answer for the user to fill in. Operation: render. " +
        "Parameters: title (string, optional — a heading for the form); " +
        "submitLabel (string, optional — the submit button text, defaults to 'Submit'); " +
        "fields (array, required — one object per field with: name (string, required — a machine key); " +
        "label (string, optional — the visible label); type (string, required — one of 'text', " +
        "'textarea', 'number', 'select', 'checkbox', 'date'); required (boolean, optional); " +
        "options (array of strings, required when type is 'select')). " +
        "The user's answers are NOT returned here; they arrive as the user's next message after they " +
        "submit. Use this to collect structured input instead of asking for many values in prose.";

    /// <inheritdoc />
    protected override string NoClientMessage =>
        "No client is connected to this conversation, so a form cannot be displayed.";

    /// <inheritdoc />
    protected override string TimeoutMessage => "The client did not display the form in time.";

    /// <inheritdoc />
    // Validate the structure from the serialized JSON so nested `fields` are inspected deterministically
    // regardless of how the tool arguments were deserialized into the dictionary.
    protected override string? ValidateArguments(
        IReadOnlyDictionary<string, object?> parameters, string argumentsJson)
        => ValidateFields(argumentsJson);

    /// <summary>Validates the serialized <c>fields</c> array; returns an error message, or null when valid.</summary>
    private static string? ValidateFields(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        if (!doc.RootElement.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
            return "Provide a non-empty 'fields' array describing the form.";

        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                return "Each form field must be an object.";

            if (!field.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
                return "Each form field needs a non-empty 'name'.";

            if (!field.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !AllowedFieldTypes.Contains(type.GetString()!))
                return $"Each form field 'type' must be one of: {string.Join(", ", AllowedFieldTypes)}.";

            if (type.GetString() == "select"
                && (!field.TryGetProperty("options", out var options)
                    || options.ValueKind != JsonValueKind.Array
                    || options.GetArrayLength() == 0))
                return "A 'select' field needs a non-empty 'options' array.";
        }

        return null;
    }
}
