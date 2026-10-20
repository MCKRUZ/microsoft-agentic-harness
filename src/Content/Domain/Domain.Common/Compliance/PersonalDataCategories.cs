namespace Domain.Common.Compliance;

/// <summary>
/// The kinds of personal data a store can hold, as declared on <see cref="PersonalDataStoreAttribute"/>.
/// </summary>
/// <remarks>
/// Coarse on purpose: the categories a privacy team needs to decide retention, erasure and lawful basis, not a
/// schema. A store declares every category it can hold under any configuration, not only its default one.
/// </remarks>
[Flags]
public enum PersonalDataCategories
{
    /// <summary>Holds no personal data. Only meaningful for a store that is registered so it is visible.</summary>
    None = 0,

    /// <summary>User, tenant, session or conversation identifiers that can be linked back to a person.</summary>
    Identifiers = 1 << 0,

    /// <summary>What people wrote to the agent, or what the agent wrote back to them.</summary>
    ConversationContent = 1 << 1,

    /// <summary>Arguments passed to tools and what the tools returned.</summary>
    ToolInputsAndOutputs = 1 << 2,

    /// <summary>Facts, memories or summaries derived from people's content, including model-inferred ones.</summary>
    DerivedKnowledge = 1 << 3,

    /// <summary>Names or identities of people acting in a workflow, such as approvers.</summary>
    PeopleNames = 1 << 4,

    /// <summary>URLs, hosts or addresses a person or agent reached.</summary>
    NetworkTargets = 1 << 5,

    /// <summary>Documents or files people supplied, or chunks and indexes built from them.</summary>
    DocumentContent = 1 << 6,

    /// <summary>Free-form operational text (logs, traces) that can carry any of the above.</summary>
    DiagnosticText = 1 << 7,
}
