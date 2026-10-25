using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>Makes a copy of everything the harness can find for one person (#902).</summary>
public interface IPersonalDataExport
{
    /// <summary>Collects <paramref name="ownerId"/>'s data from every registered store, with a manifest of what each returned.</summary>
    /// <param name="ownerId">The person, exactly as their identity provider issues the id.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The copy and its manifest.</returns>
    Task<PersonalDataExport> ExportOwnerAsync(string ownerId, CancellationToken cancellationToken);
}
