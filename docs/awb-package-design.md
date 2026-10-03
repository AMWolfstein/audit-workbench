# Audit Workbench portable package (`.awb`)

> **Superseded:** the proposed engagement-rooted package was replaced by the implemented
> client-rooted [`AWB-CLIENT/1.0`](client-handover-package.md) format (ADR-027).

An `.awb` is a portable, point-in-time handover package. It is **not** the live database and
must never be mounted for concurrent editing. `IClientHandoverPackageService` exports a company
and all of its financial-year engagements; partial-client and engagement-only handovers are not
supported in version 1.0.

The original design principles remain in force: a canonical manifest with per-file SHA-256
hashes; a consistent export snapshot; fixed archive paths and decompression limits; validation
before mutation; one atomic import transaction; preserved attribution and finalization evidence;
and destination-native export/import audit events. Credentials, sessions, server secrets,
connection strings, and unrelated clients never leave the source workspace.

Existing `AWB-BACKUP/1.0` SQLite folder backups remain whole-workspace local backups and are not
handover packages. Encryption/signing, key custody, merge/hand-back semantics, and partial import
remain future work.
