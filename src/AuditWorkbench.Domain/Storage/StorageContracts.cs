namespace AuditWorkbench.Domain.Storage;

/// <summary>
/// Stores evidence/source-file bytes outside the ordinary audit tables. The
/// contract lives in the domain so the infrastructure can implement it without
/// the application layer owning file I/O.
/// <para>
/// Implementations must return a <em>server-generated</em> storage location: the
/// application never trusts a client path. Bytes are addressed by their digest so
/// the same upload received twice is stored once.
/// </para>
/// </summary>
public interface IFileStorage
{
    Task<StoredFile> PutAsync(Guid engagementId, Stream content, string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageLocation, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageLocation, CancellationToken cancellationToken = default);
}

/// <summary>Location and integrity of stored bytes.</summary>
public sealed record StoredFile(string StorageLocation, long SizeBytes, string Sha256);
