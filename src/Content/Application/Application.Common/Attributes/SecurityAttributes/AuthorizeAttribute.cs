namespace Application.Common.Attributes.SecurityAttributes;

/// <summary>
/// Marks a MediatR request as requiring authorization. Consumed by
/// <c>AuthorizationBehavior</c> to enforce role-based and policy-based access control.
/// </summary>
/// <remarks>
/// Multiple attributes can be applied to a single request. Role checks use OR logic
/// within a single attribute (any matching role passes) and AND logic across attributes
/// (all attribute checks must pass). Policy checks use AND logic (all policies must be satisfied).
/// </remarks>
/// <example>
/// <code>
/// [Authorize(Roles = "Admin,Manager")]
/// [Authorize(Policy = "CanEditOrders")]
/// public record UpdateOrderCommand : IRequest&lt;Result&lt;Order&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class AuthorizeAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the comma-separated roles required. Any matching role satisfies
    /// this attribute's role check.
    /// </summary>
    public string Roles { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the authorization policy name required.
    /// </summary>
    public string Policy { get; set; } = string.Empty;
}
