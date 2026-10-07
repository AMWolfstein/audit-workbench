using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Infrastructure.Backup;
using AuditWorkbench.Infrastructure.Workspace;

namespace AuditWorkbench.Application.Backup;

/// <summary>
/// Explicit, user-initiated backup of the local workspace (FR-M19). No
/// administrator rights, scheduled task or service is involved.
/// </summary>
public sealed class BackupService
{
    private readonly SqliteBackupWriter _writer;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly WorkspacePaths _paths;
    private readonly EngagementAuthorizationService _authorization;

    public BackupService(
        SqliteBackupWriter writer,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        WorkspacePaths paths,
        EngagementAuthorizationService authorization)
    {
        _writer = writer;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _paths = paths;
        _authorization = authorization;
    }

    public string DefaultDestination => _paths.BackupsDirectory;

    public async Task<BackupPackage> CreateAsync(
        string? destinationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // A backup contains every engagement, so it needs workspace privilege (ADR-024).
        await _authorization.RequireWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false);

        var package = await _writer.CreateAsync(destinationDirectory, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.ExecuteAsync(token => _auditTrail.AppendAsync(
                AuditEventType.BackupCreated,
                AuditEntityType.Workspace,
                package.Name,
                $"Backup package '{package.Name}' created.",
                details: AuditDetails.Empty()
                    .With("destination_class",
                        string.IsNullOrWhiteSpace(destinationDirectory) ? "DEFAULT_WORKSPACE_FOLDER" : "USER_SELECTED_FOLDER")
                    .With("size_bytes", package.SizeBytes),
                cancellationToken: token), cancellationToken)
            .ConfigureAwait(false);

        return package;
    }

    public async Task<IReadOnlyList<BackupPackage>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _authorization.RequireWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false);
        return SqliteBackupWriter.List(_paths.BackupsDirectory);
    }

    public async Task<BackupVerificationResult> VerifyAsync(string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false);
        return SqliteBackupWriter.Verify(packageDirectory);
    }
}
