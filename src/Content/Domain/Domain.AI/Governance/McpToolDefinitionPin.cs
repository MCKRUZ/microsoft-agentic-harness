namespace Domain.AI.Governance;

/// <summary>
/// The last-seen definition hash pair for one MCP tool, used to detect a rug pull — a definition that
/// changes after the tool has already earned a place in the surface. Hashing the schema separately
/// from the description matters: an attacker who knows the description is the part that gets read
/// moves the payload into a parameter description instead, which changes the schema hash while
/// leaving the description hash untouched.
/// </summary>
/// <param name="DescriptionHash">Hash of the tool's description text.</param>
/// <param name="SchemaHash">Hash of the tool's parameter schema text.</param>
public sealed record McpToolDefinitionPin(string DescriptionHash, string SchemaHash);
