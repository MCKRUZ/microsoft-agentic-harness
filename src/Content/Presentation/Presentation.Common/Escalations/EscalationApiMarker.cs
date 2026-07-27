namespace Presentation.Common.Escalations;

/// <summary>
/// DI marker whose presence records that a host deliberately opted into serving the escalation
/// decision API. Registered only by
/// <see cref="EscalationApiMvcBuilderExtensions.AddEscalationApi"/>;
/// <see cref="RequiresEscalationApiOptInAttribute"/> checks for it at route-match time and
/// un-matches every <see cref="EscalationsController"/> route when it is absent.
/// </summary>
/// <remarks>
/// This runtime gate exists because compile-time placement cannot deliver the opt-in: the Web
/// SDK auto-generates an <c>ApplicationPartAttribute</c> for every referenced assembly that
/// references MVC, so any MVC host referencing <c>Presentation.Common</c> discovers this
/// assembly's controllers whether or not it called <c>AddApplicationPart</c>. Escalation state
/// is an in-process singleton that only specific hosts own; a host that merely composes shared
/// services must not accidentally expose approval routes. Fail-closed: no marker, no routes
/// (404 before authentication, indistinguishable from a host without the API).
/// </remarks>
public sealed class EscalationApiMarker
{
}
