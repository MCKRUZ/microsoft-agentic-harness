using Application.AI.Common.Interfaces.Audit;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Domain.AI.Audit;

namespace Presentation.AuditArchiver;

/// <summary>
/// The audit archive in an Azure Blob container under a locked time-based retention policy (#416). Each file is one
/// upload that succeeds only if no blob of that name exists, with an MD5 checksum the service verifies, so a file is
/// in the archive whole or not at all. Listing through the Blob API is strongly consistent, unlike a mounted folder.
/// </summary>
/// <remarks>
/// Authenticated as the archiver's own identity (Storage Blob Data Contributor on this container only). The
/// application's identity must have no role that can write here, and no control-plane role on the account that could
/// read its keys; turn off shared-key access on the account.
/// </remarks>
internal sealed class BlobAuditArchiveStore : IAuditArchiveStore
{
    private const int SingleUploadLimit = 256 * 1024 * 1024;
    private readonly BlobContainerClient _container;

    /// <summary>Initializes the store.</summary>
    /// <param name="container">The archive container.</param>
    public BlobAuditArchiveStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditArchiveItem>> ListAsync(string folder, CancellationToken cancellationToken)
    {
        var prefix = string.IsNullOrEmpty(folder) ? string.Empty : folder.TrimEnd('/') + "/";
        var items = new List<AuditArchiveItem>();
        await foreach (var entry in _container.GetBlobsByHierarchyAsync(
                           BlobTraits.None, BlobStates.None, "/", prefix, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsBlob)
            {
                items.Add(new AuditArchiveItem(
                    entry.Blob.Name[prefix.Length..],
                    entry.Blob.Properties.ContentLength ?? 0,
                    entry.Blob.Properties.LastModified ?? DateTimeOffset.MinValue));
            }
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var download = await _container.GetBlobClient(path).DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return download.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AuditArchivePut> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (content.Length > SingleUploadLimit)
            throw new IOException($"{path} is larger than one upload allows ({SingleUploadLimit} bytes).");

        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            TransferOptions = new StorageTransferOptions { InitialTransferSize = SingleUploadLimit, MaximumTransferSize = SingleUploadLimit },
            TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
        };

        try
        {
            await _container.GetBlobClient(path).UploadAsync(new BinaryData(content), options, cancellationToken).ConfigureAwait(false);
            return AuditArchivePut.Created;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var existing = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return existing is not null && existing.AsSpan().SequenceEqual(content.Span)
                ? AuditArchivePut.Identical
                : AuditArchivePut.Different;
        }
    }
}
