namespace Presentation.Common.SkillRegistry;

/// <summary>
/// DI marker whose presence records that a host deliberately opted into serving the skill registry
/// operator API. Registered only by
/// <see cref="SkillRegistryApiMvcBuilderExtensions.AddSkillRegistryApi"/>;
/// <see cref="RequiresSkillRegistryApiOptInAttribute"/> checks for it at route-match time and
/// un-matches every <see cref="SkillRegistryController"/> route when it is absent.
/// </summary>
/// <remarks>
/// This runtime gate exists because compile-time placement cannot deliver the opt-in: the Web SDK
/// auto-generates an <c>ApplicationPartAttribute</c> for every referenced assembly that references
/// MVC, so any MVC host referencing <c>Presentation.Common</c> discovers this assembly's controllers
/// whether or not it called <c>AddApplicationPart</c>. A refresh forces every configured skill path
/// to be rescanned — a host that merely composes shared services must not accidentally expose that.
/// Fail-closed: no marker, no routes (404 before authentication, indistinguishable from a host
/// without the API). Mirrors <c>AgentRegistryApiMarker</c> (issue #705).
/// </remarks>
public sealed class SkillRegistryApiMarker
{
}
