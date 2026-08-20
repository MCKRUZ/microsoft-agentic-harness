namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Marks a value <see cref="AIToolConverter"/> returned as a tool failure rather than a genuine
/// result, so <see cref="GovernedAIFunction"/> can report the correct escalation status instead of
/// treating every no-throw return as <c>Succeeded</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only reaches <see cref="GovernedAIFunction"/> intact because <c>AIToolConverter</c> pairs this
/// type with an <see cref="Microsoft.Extensions.AI.AIFunctionFactoryOptions.MarshalResult"/> delegate
/// that bypasses the framework's default return-value marshaling for it specifically. Without that,
/// this record could not survive: <c>AIFunctionFactory</c>-created functions JSON-serialize their
/// delegate's return value into a <see cref="System.Text.Json.JsonElement"/> inside their own
/// <c>InvokeCoreAsync</c> — confirmed against the SDK source, not assumed — before
/// <see cref="GovernedAIFunction"/> or any other decorator ever inspects the value, which would
/// erase any CLR type identity a marker relied on.
/// </para>
/// <para>
/// Never crosses into the framework layer beyond that: <see cref="GovernedAIFunction"/> unwraps this
/// back to <see cref="ErrorText"/> before returning, on every exit path, so the model and every
/// downstream consumer see the same plain string they always have.
/// </para>
/// </remarks>
internal sealed record ConvertedToolFailure(string ErrorText);
