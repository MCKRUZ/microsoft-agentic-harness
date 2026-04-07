using Application.Common.Interfaces.Security;
using Domain.Common;

namespace Infrastructure.Common.Services;

/// <summary>
/// Stub implementation of <see cref="IIdentityService"/> for development and testing.
/// Returns predefined values for all operations.
/// </summary>
/// <remarks>
/// Replace with a real implementation that integrates with an identity provider
/// (Azure Entra ID, Auth0, etc.) before production deployment.
/// </remarks>
public sealed class IdentityService : IIdentityService
{
    /// <inheritdoc />
    /// <remarks>Stub: always returns "Development User".</remarks>
    public Task<string?> GetUserNameAsync(string userId) =>
        Task.FromResult<string?>("Development User");

    /// <inheritdoc />
    /// <remarks>Stub: always returns <c>false</c>.</remarks>
    public Task<bool> IsInRoleAsync(string userId, string role) =>
        Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Stub: always returns <c>false</c>.</remarks>
    public Task<bool> AuthorizeAsync(string userId, string policyName) =>
        Task.FromResult(false);

    /// <inheritdoc />
    /// <remarks>Stub: always returns success with a fixed user ID.</remarks>
    public Task<Result<string>> CreateUserAsync(string userName, string password) =>
        Task.FromResult(Result<string>.Success("dev-user-1"));

    /// <inheritdoc />
    /// <remarks>Stub: always returns success.</remarks>
    public Task<Result> DeleteUserAsync(string userId) =>
        Task.FromResult(Result.Success());
}
