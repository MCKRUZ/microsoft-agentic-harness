namespace Domain.Common.Enums;

/// <summary>
/// Defines the permission levels used by the policy-based authorization system.
/// </summary>
/// <remarks>
/// These permissions are encoded into ASP.NET Core policy names by
/// <c>PermissionAuthorizeAttribute</c> and evaluated by <c>PermissionAuthHandler</c>.
/// <para>
/// The integer values are serialized into policy strings (e.g., "Permission0-2")
/// enabling multiple permissions per attribute without a combinatorial explosion of policies.
/// </para>
/// </remarks>
public enum AuthPermissions
{
    /// <summary>
    /// Basic access to the application. Granted to all authenticated users.
    /// </summary>
    Access,

    /// <summary>
    /// User has agreed to the application's terms and conditions.
    /// </summary>
    TermsAgreement,

    /// <summary>
    /// Administrative privileges granting full access to all features.
    /// </summary>
    Admin,
}
