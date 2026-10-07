using System.Security.Cryptography;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Storage;
using AuditWorkbench.Infrastructure.Workspace;

namespace AuditWorkbench.Infrastructure.Storage;

/// <summary>
/// Engagement-scoped, content-addressed attachment store under the workspace
/// folder (NFR-04). Bytes are written to
/// <c>attachments/{engagement}/{sha256}{extension}</c> so the same file received
/// twice occupies one blob; the database keeps the provenance row.
/// <para>
/// Stored bytes are never executed, never served directly and never addressed by
/// a client-supplied path: the location is generated here.
/// </para>
/// </summary>
public sealed class AttachmentStore : IFileStorage
{
    private readonly WorkspacePaths _paths;

    public AttachmentStore(WorkspacePaths paths) => _paths = paths;

    public async Task<StoredFile> PutAsync(Guid engagementId, Stream content, string contentType,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
        {
            throw new ValidationException("There is no file content to store.");
        }

        Directory.CreateDirectory(_paths.AttachmentsDirectory);
        var engagementDirectory = Path.Combine(_paths.AttachmentsDirectory, engagementId.ToString("N"));
        Directory.CreateDirectory(engagementDirectory);

        var temporary = Path.Combine(engagementDirectory, $"{Guid.NewGuid():N}.uploading");
        long sizeBytes;
        string sha256;
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha256 = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
                sizeBytes = target.Length;
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        var finalName = $"{sha256}{ExtensionFor(contentType)}";
        var finalPath = Path.Combine(engagementDirectory, finalName);
        if (File.Exists(finalPath))
        {
            // The identical file is already stored: keep the existing blob.
            TryDelete(temporary);
        }
        else
        {
            File.Move(temporary, finalPath);
        }

        var relative = Path.GetRelativePath(_paths.RootDirectory, finalPath).Replace('\\', '/');
        return new StoredFile(relative, sizeBytes, sha256);
    }

    public Task<Stream> OpenReadAsync(string storageLocation, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageLocation);
        if (!File.Exists(path))
        {
            throw new NotFoundException("The stored source file for this import is missing from the workspace.");
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageLocation, CancellationToken cancellationToken = default)
    {
        var path = Resolve(storageLocation);
        TryDelete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a stored location inside the attachments root and refuses anything
    /// that would escape it (path traversal, absolute paths, drive-qualified paths).
    /// </summary>
    private string Resolve(string storageLocation)
    {
        if (string.IsNullOrWhiteSpace(storageLocation))
        {
            throw new ValidationException("The stored file location is missing.");
        }

        var root = Path.GetFullPath(_paths.AttachmentsDirectory);
        var candidate = Path.GetFullPath(Path.Combine(_paths.RootDirectory, storageLocation.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("The stored file location is not inside this workspace.");
        }

        return candidate;
    }

    private static string ExtensionFor(string? contentType) => contentType switch
    {
        "text/csv" => ".csv",
        "text/plain" => ".txt",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        _ => ".bin",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless; the next upload uses a new name.
        }
    }
}
