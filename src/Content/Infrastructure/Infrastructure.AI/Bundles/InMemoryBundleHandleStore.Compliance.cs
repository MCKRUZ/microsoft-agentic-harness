using System.Text.Json;
using Application.AI.Common.Interfaces.Compliance;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Bundles;

/// <summary>Right-to-erasure and export (#990): the bundles the person staged.</summary>
public sealed partial class InMemoryBundleHandleStore : IPersonalDataEraser, IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string ErasureStoreName => "staged bundles";

    /// <inheritdoc />
    public string ExportStoreName => ErasureStoreName;

    /// <inheritdoc />
    /// <remarks>Nothing else is found through a handle: the bundle runs that name one are erased by their own owner.</remarks>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.Owner;

    /// <inheritdoc />
    public string KeptRunsStoreName => "staged bundles still held";

    /// <inheritdoc />
    public string KeptRunsReason =>
        "a bundle the person staged was kept: a run was still using it, and it is removed when that run ends, or it was staged while erasure ran; run erasure again once the person's bundle runs have ended";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Removes every handle the person staged, matched through <see cref="ScopeIdentity.AreSame"/>, the way
    /// <see cref="Remove"/> does: a handle no run holds is evicted and its staging folder deleted now; one a run holds a
    /// lease on is marked removed and goes when the run releases it, so a run in flight never loses the definitions it is
    /// reading. What is still held is counted by <see cref="CountRunsKeptAsync"/>, and the receipt says to run erasure
    /// again.
    /// </para>
    /// <para>
    /// A bundle run the person queued holds no lease until it starts, so it now finds its handle gone and fails at
    /// dispatch, as it would had the handle expired. That is deliberate: the run would otherwise start after the person
    /// was erased. Its record is then finished, and the next erasure request removes it.
    /// </para>
    /// </remarks>
    /// <returns>How many handles were evicted now; a leased one counts once its run releases it.</returns>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var evicted = OwnedBy(subject.OwnerId).Count(owned => MarkRemoved(owned.Handle, owned.Entry));
        return Task.FromResult(evicted);
    }

    /// <inheritdoc />
    public Task<int> CountRunsKeptAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return Task.FromResult(OwnedBy(ownerId).Count);
    }

    /// <summary>
    /// Every handle the person staged, still usable or not, matched the way erasure matches. What the bundle defined is
    /// named (agent, skills, plugins, MCP servers) but not the server folder it was extracted to.
    /// </summary>
    /// <param name="subject">The person the copy is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One record per handle.</returns>
    public Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        IReadOnlyList<JsonElement> records = [.. OwnedBy(subject.OwnerId).Select(owned =>
        {
            // The changing fields are read under the entry's lock and serialized after it, so a run starting or ending
            // on the handle never waits behind the copy. The bundle and the owner never change.
            DateTimeOffset expiresAt;
            bool inUse;
            lock (owned.Entry)
            {
                expiresAt = owned.Entry.ExpiresAt;
                inUse = owned.Entry.LeaseCount > 0;
            }

            var bundle = owned.Entry.Bundle;
            return JsonSerializer.SerializeToElement(new
            {
                owned.Handle,
                owned.Entry.OwnerId,
                AgentId = bundle.Agent.Id,
                AgentName = bundle.Agent.Name,
                Skills = bundle.OwnedSkills.Select(s => s.Name).ToList(),
                Plugins = bundle.PluginManifests.Select(p => p.Name).ToList(),
                bundle.McpServerNames,
                ExpiresAt = expiresAt,
                InUseByRun = inUse,
            }, ExportJson);
        })];
        return Task.FromResult(records);
    }

    /// <summary>A snapshot of every handle <paramref name="ownerId"/> staged, each owner read under its entry's lock.</summary>
    private List<(string Handle, HandleEntry Entry)> OwnedBy(string ownerId)
    {
        var owned = new List<(string, HandleEntry)>();
        foreach (var (handle, entry) in _entries)
        {
            lock (entry)
            {
                if (ScopeIdentity.AreSame(entry.OwnerId, ownerId))
                    owned.Add((handle, entry));
            }
        }

        return owned;
    }
}
