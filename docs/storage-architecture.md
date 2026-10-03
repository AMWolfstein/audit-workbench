# Storage architecture

Storage is split into relational metadata and binary content.

- **Relational persistence:** EF Core model selected in infrastructure as PostgreSQL (preferred production), SQL Server, or SQLite (test/local development).
- **File storage:** application-level `IFileStorage`; domain objects never use filesystem paths or object-store SDKs.
- **Portable package:** `IAuditEngagementPackageService`; `.awb` is import/export/archive, never a mounted live database.

Future evidence metadata belongs in the central database: evidence ID, engagement ID, related object, original filename, content type, size, SHA-256, uploader, upload time, opaque storage location, description, and status. Bytes belong in a local filesystem, network share, central file server, or object storage implementation. Storage locations are opaque keys, not client-controlled paths.

A future upload protocol must stream to quarantine, calculate SHA-256, malware-check per policy, commit metadata and an immutable storage object, and compensate safely on failure. Authorization is checked before both metadata and byte access. Finalization/package export includes hashes and verifies content without copying binaries into ordinary audit tables.

The existing `SqliteBackupWriter` remains a legacy local-workspace backup mechanism for MVP compatibility. It is not the central-server backup strategy and is not the `.awb` format. Production database/PITR and storage backups are operational controls; engagement-level portable export is a separate application operation.
