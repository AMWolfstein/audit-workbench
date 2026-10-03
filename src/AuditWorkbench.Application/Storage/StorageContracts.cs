namespace AuditWorkbench.Application.Storage;

/// <summary>Stores evidence/package bytes outside ordinary audit tables.</summary>
public interface IFileStorage
{
    Task<StoredFile> PutAsync(Guid engagementId, Stream content, string contentType,
        CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storageLocation, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageLocation, CancellationToken cancellationToken = default);
}

public sealed record StoredFile(string StorageLocation, long SizeBytes, string Sha256);

/// <summary>Portable .awb import/export boundary; an implementation must never expose a live database file.</summary>
public interface IAuditEngagementPackageService
{
    Task ExportAsync(Guid engagementId, Stream destination, CancellationToken cancellationToken = default);
    Task<Guid> ImportAsync(Stream package, CancellationToken cancellationToken = default);
}
