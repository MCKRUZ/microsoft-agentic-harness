namespace Presentation.Common.ChangeProposals;

/// <summary>
/// DI marker whose presence records that a host deliberately opted into serving the
/// change-proposal decision API. Registered only by
/// <see cref="ChangeProposalApiMvcBuilderExtensions.AddChangeProposalApi"/>;
/// <see cref="RequiresChangeProposalApiOptInAttribute"/> checks for it at route-match time and
/// un-matches every <see cref="ChangeProposalsController"/> route when it is absent.
/// </summary>
/// <remarks>
/// This runtime gate exists because compile-time placement cannot deliver the opt-in: the Web
/// SDK auto-generates an <c>ApplicationPartAttribute</c> for every referenced assembly that
/// references MVC, so any MVC host referencing <c>Presentation.Common</c> discovers this
/// assembly's controllers whether or not it called <c>AddApplicationPart</c>. Change-proposal
/// state lives in an in-process store (<c>InMemoryChangeProposalStore</c> by default) that only
/// specific hosts own; a host that merely composes shared services must not accidentally expose
/// decision routes. Fail-closed: no marker, no routes (404 before authentication,
/// indistinguishable from a host without the API).
/// </remarks>
public sealed class ChangeProposalApiMarker
{
}
