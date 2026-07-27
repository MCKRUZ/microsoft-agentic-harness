namespace Presentation.Common.Drift;

/// <summary>
/// DI marker whose presence records that a host deliberately opted into serving the drift
/// monitoring API. Registered only by
/// <see cref="DriftApiMvcBuilderExtensions.AddDriftApi"/>;
/// <see cref="RequiresDriftApiOptInAttribute"/> checks for it at route-match time and
/// un-matches every <see cref="DriftController"/> route when it is absent.
/// </summary>
/// <remarks>
/// This runtime gate exists because compile-time placement cannot deliver the opt-in: the Web
/// SDK auto-generates an <c>ApplicationPartAttribute</c> for every referenced assembly that
/// references MVC, so any MVC host referencing <c>Presentation.Common</c> discovers this
/// assembly's controllers whether or not it called <c>AddApplicationPart</c>. The drift write
/// endpoints move EWMA state and feed future baselines — a host that merely composes shared
/// services must not accidentally expose them. Fail-closed: no marker, no routes (404 before
/// authentication, indistinguishable from a host without the API).
/// </remarks>
public sealed class DriftApiMarker
{
}
