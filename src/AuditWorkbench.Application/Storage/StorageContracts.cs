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

// Engagement-rooted package transfer was superseded by
// Handover.IClientHandoverPackageService (AWB-CLIENT/1.0). Evidence storage
// remains engagement-scoped; client handover is intentionally a separate boundary.
