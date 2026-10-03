# Audit Engagement Package (`.awb`) design

> **Superseded in part (proposed):** the export root is changing from one engagement to one client. See [client-handover-package.md](client-handover-package.md). The principles below still apply.

An `.awb` is a portable, point-in-time engagement export/backup/archive/transfer package. It is **not** the live multi-user database and must never be opened for concurrent editing.

`IAuditEngagementPackageService` establishes the application boundary. A future versioned container should include a canonical manifest, company/engagement/year metadata, audit records, working-paper data when those modules exist, evidence files, review notes, and the engagement audit-event subset. Every payload entry records path, byte length, SHA-256, media type, and logical owner. The manifest records package-format version, export operation/correlation ID, source engagement ID, export actor/time, schema versions, finalization digest, and hash algorithm.

Export requires engagement membership plus `EXPORT_ENGAGEMENT`, reads a consistent central-database snapshot, obtains immutable file versions, computes/verifies hashes, writes to a staging stream, then atomically publishes. It never exports credentials, sessions, server secrets, connection strings, or unrelated engagements.

Import requires `IMPORT_ENGAGEMENT` at the appropriate administrative scope, validates archive paths/sizes, manifest/schema compatibility and every hash before mutation, resolves IDs and collisions explicitly, and writes through application services in one governed operation. Import preserves original attribution and audit history while recording a new import event; it must not impersonate historical actors.

Encryption/signing, key custody, duplicate/merge semantics, partial import, retention, and exact container serialization are future decisions. Existing `AWB-BACKUP/1.0` SQLite folder backups are legacy local backups and are not renamed or falsely treated as this package.
