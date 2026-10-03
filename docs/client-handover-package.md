# Client Handover Package (`AWB-CLIENT/1.0`) — design

> **Status: Implemented (AWB-CLIENT/1.0).** Migration `0005_client_handover`, the
> `IClientHandoverPackageService`, Razor UI, and package tests implement this specification.
> Recommended decisions D-1 to D-3 were accepted for version 1.0. The executable schema and
> application code remain the source of truth.

## 1. Goal and change of export root

A complete **client** must be exportable from one Audit Workbench instance and imported
into another, so another auditor can receive the client, see its complete history and
continue the audit, including creating and finalizing subsequent financial years.

```text
Instance A → Export Client → Client Handover Package → transfer
           → Import Client into Instance B → full history → create/continue next year
```

The **client (`company`) is the export root**, not the financial year / engagement.

### Existing material: retained, replaced, generalized

The repository contains no "Financial Year Extract" implementation. The closest material:

| Existing piece | What it is | Decision |
|---|---|---|
| `docs/awb-package-design.md`, ADR-023 | Engagement-rooted `.awb` design (design only) | **Generalize**: keep manifest-with-hashes, staging + atomic publish, never secrets, validate before mutation, preserve attribution, record an import event. Root changes from engagement to client. |
| `IAuditEngagementPackageService.ExportAsync(Guid engagementId, …)` | Interface only, never implemented | **Replace** with `IClientHandoverPackageService` keyed by `companyId`. |
| `AWB-MANIFEST/1.0` finalization manifest + root digest | Implemented in .NET and the Python harness | **Retain unchanged**; it is the primary integrity anchor of a handover. |
| `SqliteBackupWriter` (`AWB-BACKUP/1.0`) | Whole-workspace database copy | **Retain** as the local backup. Never a handover: it contains every client. |
| `EXPORT_ENGAGEMENT` / `IMPORT_ENGAGEMENT` permissions | Engagement-scoped | Reuse `EXPORT_ENGAGEMENT`. `IMPORT_ENGAGEMENT` cannot gate an import because the destination has no engagement yet. |

## 2. Handover boundary

The client root is `company`. In this schema "client" is the business name for `Company`;
no separate client entity exists.

| Table | Ownership | Included | Selection |
|---|---|---|---|
| `company` | Client root | Yes | `company_id = C` |
| `engagement` | Client; the financial-year boundary (ADR-001) | Yes | `company_id = C`, all years and statuses |
| `financial_year` | **Shared reference** (global unique definition) | Referenced rows only | used by the client's engagements |
| `account` | Engagement | Yes | engagement in set |
| `financial_data` | Engagement, append-only | Yes, every revision | engagement in set |
| `prior_year_relationship` | Engagement (current year) | Yes | both ends in set (same-company trigger) |
| `finalization_manifest` | Engagement | Yes, verbatim (`canonical_content`, `root_digest`) | engagement in set |
| `engagement_member` | Engagement | **History only** (section 7) | engagement in set |
| `assignment` | Engagement | **History only** (section 7) | engagement in set |
| `audit_event` | Workspace-wide hash chain | Client subset, archived and read-only (section 6) | `company_id = C` or `engagement_id` in set |
| `app_user` | Global identity | Minimal attribution only | ids referenced by included rows |
| `app_role`, `role_permission` | Global seed data | No | memberships map by `role_key` |
| `schema_migration`, `workspace_metadata` | System | No | — |

* **Client-owned:** `company` and its `engagement` rows.
* **Engagement-owned:** `account`, `financial_data`, `prior_year_relationship`,
  `finalization_manifest`, `engagement_member`, `assignment`.
* **Financial-year-owned:** the year boundary is the `engagement`. `financial_year` is a
  shared period definition, included by reference only.
* **Documents and working papers:** none exist yet (no evidence or working-paper table;
  `IFileStorage` has no implementation). Format 1.0 reserves `files/` and `manifest.files[]`
  for them but ships none.

## 3. Package structure

ZIP container, extension `.awb`. Data files are explicit versioned transfer schemas, not
table dumps, so the package format is decoupled from the database schema.

```text
manifest.json
data/company.json
data/financial_years.json
data/engagements.json
data/accounts.jsonl
data/financial_data.jsonl            every revision, ordered by (engagement, account, revision_no)
data/prior_year_relationships.json
data/finalization_manifests.json     canonical_content byte-exact
data/principals.json                 user_id, username, display_name (attribution only)
history/team_members.json            source memberships (history only)
history/assignments.json             source assignments (history only)
audit/events.jsonl                   client audit subset, all fields including both hashes
audit/source_chain.json              source head sequence/hash, export-time verification, subset count
files/                               reserved, empty in 1.0
```

```json
{
  "package_format": "AWB-CLIENT/1.0",
  "package_id": "<uuid>",
  "created_at_utc": "…",
  "exported_by": { "user_id": "…", "display_name": "…" },
  "source": { "schema_version": "0004_engagement_currency_guard", "workspace_format_version": "…",
              "application_version": "…" },
  "requires": { "min_schema_version": "<handover migration id>", "manifest_versions": ["AWB-MANIFEST/1.0"] },
  "client": { "company_id": "…", "short_name": "…", "legal_name": "…", "status": "ACTIVE" },
  "engagements": [ { "engagement_id": "…", "financial_year_id": "…", "label": "FY2026",
                     "period_end": "…", "status": "FINALIZED", "finalization_digest": "…" } ],
  "counts": { "accounts": 0, "financial_data": 0, "audit_events": 0, "principals": 0 },
  "hash_algorithm": "SHA-256",
  "files": [ { "path": "data/accounts.jsonl", "bytes": 0, "sha256": "…",
               "media_type": "application/x-ndjson", "owner": "engagement" } ]
}
```

**Versioning.** `package_format` is `AWB-CLIENT/<major>.<minor>`; an unknown major is
rejected and a newer minor is rejected unless declared compatible. The destination must be at
or above `requires.min_schema_version` and support every listed manifest version. Files use
canonical JSON: UTF-8 without BOM, sorted keys, LF line endings.

## 4. Identity strategy

**Preserve every client-owned id; remap nothing in 1.0.** The AWB-MANIFEST digest embeds
`engagement_id`, `company_id`, `financial_year_id` and `prior_engagement_id`; remapping would
break every finalized year's digest and the `prior_root_digest` chain. Ids are random UUIDs,
so a collision means the client was already imported or originated in the destination.

| Object | Strategy |
|---|---|
| company, engagement, account, financial_data, prior link, manifest | Preserve; existing id → conflict, reject |
| `financial_year` | same id and identical definition → reuse; same definition, different id → preserve and insert (D-1) |
| `app_user` principals | same id and same username → reuse; id or username used by someone else → conflict; otherwise insert a DISABLED external principal (D-3) |
| roles | map by `role_key`, never by id |
| `row_version`, timestamps, `created_by` etc. | preserved |

References inside the package are the same UUID strings as the database columns. Every
reference must resolve inside the package or to an allowed destination row (financial year,
principal) before any write.

**Duplicate-client detection, in order:** `CLIENT_ALREADY_EXISTS` (company id present) →
`CLIENT_SHORT_NAME_TAKEN` (case-insensitive short name used by another company; renaming is
impossible because the short name is inside the digest) → `ID_COLLISION` (any other row id) →
`PACKAGE_ALREADY_IMPORTED` (package id in `client_import`). Merge or hand-back is out of scope
for 1.0 (D-2).

Known caveat: `LocalUser.LocalActorId` is the same fixed GUID in every instance, so the local
development identity is reused rather than kept distinct. This affects development only.

## 5. Import dependency order

Database triggers stay enabled at all times.

```text
validate everything, no writes (section 6)
BEGIN
 1. principals → app_user (reuse or insert DISABLED external principal)
 2. financial_year (reuse or insert)
 3. company
 4. for each engagement ordered by period_end ascending:
    a. engagement; a source FINALIZED engagement is inserted as DRAFT with the
       finalization columns NULL (CHECK constraint and account/value lock triggers)
    b. account
    c. financial_data ordered by (account, revision_no) (revision-chain trigger, supersedes FK)
    d. prior_year_relationship with current = this engagement (prior already FINALIZED,
       current not yet, as the triggers require)
    e. engagement_member for the importing user as PARTNER (must precede finalization:
       trg_member_insert_locked)
    f. if the source was FINALIZED: insert finalization_manifest verbatim, then UPDATE to
       FINALIZED with the source finalized_at/by, digest, manifest version and row_version;
       trg_engagement_finalization_requires_manifest re-checks the digest
 5. imported audit history and the client_import record
 6. recompute AWB-MANIFEST/1.0 for every finalized engagement from the written rows;
    any mismatch → ROLLBACK
 7. append CLIENT_IMPORTED to the destination's own audit chain
COMMIT
```

## 6. Security boundary and integrity

**Never leaves the source instance:** other clients' rows, events and unused financial years;
`schema_migration`, `workspace_metadata`, workspace/backup paths and machine names; connection
strings, `AuditDatabaseOptions`/appsettings, data-protection keys, sessions or cookies; any
future credential or token. The exporter whitelists columns, so a credential column added
later cannot leak. Also excluded: `app_user.email`, `status`, `is_local_demo` (data
minimization), and `BACKUP_CREATED`, `USER_*`, `DEMO_DATA_SEEDED` and other events not scoped
to the client.

**Authorization.** Export requires `EXPORT_ENGAGEMENT` on **every** engagement of the client;
a partial client is never exported. Import requires workspace privilege (ADR-024), later a
dedicated administrator permission.

**Container safety.** Fixed entry-name whitelist; no `..`, absolute paths or symlinks;
per-entry, total and compression-ratio limits; JSON parsing only, nothing is executed.

**Integrity.**

1. Per-file SHA-256 and byte length in the manifest. The package digest is the SHA-256 of the
   exact `manifest.json` bytes, recorded in `CLIENT_EXPORTED` and `CLIENT_IMPORTED`.
2. Business integrity: for each finalized engagement the AWB-MANIFEST is rebuilt from package
   data and compared with the shipped `canonical_content` and digest, then again after the
   write. `prior_root_digest` must equal the prior engagement's digest.
3. Audit integrity: the global chain cannot be transplanted (`sequence_no` is globally unique
   and `previous_event_hash` belongs to the source chain). The client subset is stored as
   archived history keyed by source sequence and hash. Each event's own hash is recomputed,
   proving its content. Gaps in the subset are expected; the source head and its export-time
   verification travel in `source_chain.json`. `CLIENT_IMPORTED` anchors the import in the
   destination chain.
4. Limits: tamper evidence, not signatures. A holder of the file can rewrite data and every
   hash consistently. Signing and key custody remain future work (ADR-007, ADR-023).

**Validation before any change** (collect all findings, do not stop at the first): format and
version support; checksums; JSON shape and domain rules (CHECK rules, value currency equals
engagement currency, contiguous revision chains); reference closure; period ordering and
prior-year rules; manifest recomputation; destination conflicts; warning for an archived
client, which cannot receive new years.

**Transactions.** Export reads one consistent snapshot in a single read transaction, writes to
a staging file and publishes atomically. Import is one database transaction; any error rolls
everything back.

**Error reporting.** Structured `{code, severity, entity, source_id, message}` entries:
`FORMAT_UNSUPPORTED`, `SCHEMA_TOO_OLD`, `CHECKSUM_MISMATCH`, `MANIFEST_DIGEST_MISMATCH`,
`REFERENCE_DANGLING`, `CLIENT_ALREADY_EXISTS`, `CLIENT_SHORT_NAME_TAKEN`, `ID_COLLISION`,
`FINANCIAL_YEAR_CONFLICT`, `PRINCIPAL_CONFLICT`, `ROLE_UNKNOWN`, `PACKAGE_ALREADY_IMPORTED`.
Shown in the UI as a dry-run preview before confirmation. Messages never contain amounts.

## 7. Team memberships, assignments and principals

Source team members do not exist in the destination, and live memberships would grant access
the moment a principal were reactivated. Therefore:

* Source `engagement_member` and `assignment` rows travel as **history only** (`history/` in
  the package and the `client_import` record); membership changes are also in the archived
  audit events.
* The importing user receives a **PARTNER** membership on every imported engagement inside the
  import transaction, attributed to the import. This is the only way to see finalized years,
  because migration 0003 forbids adding members after finalization.
* External principals are DISABLED and marked so they can never be reactivated or given a
  membership or assignment (D-3).

## 8. Future-year continuation

After import the recipient is Partner on every year of the client, and existing code paths
apply unchanged:

* **Last year still DRAFT or IN_PROGRESS:** the recipient continues and finalizes it;
  `FinalizationService` links `prior_root_digest` to the preserved imported digest.
* **Next year:** `EngagementService.CreateAsync(companyId, "FY2027", …, priorEngagementId:
  lastFinalized)`, with the existing rules (active company, no overlap, prior finalized,
  earlier and same company). The creator becomes Partner; comparatives read preserved data.
* The digest chain continues across instances without a break, which is why ids are preserved.

## 9. Accepted decisions

* **D-1 — financial-year uniqueness.** Accepted. Migration `0005_client_handover` drops
  `ux_financial_year_definition`; ids embedded in finalized manifests remain authoritative.
  Ordinary engagement creation still reuses an exact matching definition.
* **D-2 — import target.** Accepted. Version 1.0 imports only when the destination does not
  already hold the client; merge and hand-back remain out of scope.
* **D-3 — team history and principals.** Accepted. Source memberships and assignments are
  archived history only. Imported users are disabled external principals protected by
  database triggers; only the importing user receives live Partner memberships.

## 10. Required changes

* **Database (new migrations only):**
  * `client_import` — import id, package id, package digest, source company, imported
    at/by, source manifest JSON, team-history JSON; append-only triggers.
  * `imported_audit_event` — every source audit field plus `import_id`; no foreign key on the
    actor; append-only triggers.
  * `app_user.is_external_principal`, plus triggers forbidding its reactivation, membership
    or assignment.
  * D-1: drop `ux_financial_year_definition`.
* **Domain:** handover transfer types and pure validation rules; new event types
  `CLIENT_EXPORTED` and `CLIENT_IMPORTED` (vocabulary change, sync the harness).
* **Infrastructure:** ZIP reader/writer with path and size guards; canonical JSON writer;
  snapshot exporter; `ClientHandoverImportWriter` using parameterized SQL on the shared
  connection inside the transaction, triggers active, preserving ids and timestamps without
  public mutators on domain entities.
* **Application:** `IClientHandoverPackageService` (`ExportAsync(companyId, stream)`,
  `ValidateAsync(stream)` returning the report, `ImportAsync(stream)`); authorization;
  validation; ordered import; recipient memberships; export/import events; post-write
  manifest re-verification.
* **UI:** "Export client" on company details; "Import client" page with upload, validation
  preview, explicit confirmation and a result summary.
* **Docs and harness:** an ADR superseding the engagement-rooted part of ADR-023; update
  `awb-package-design.md`; a Python reference verifier for `AWB-CLIENT/1.0`.

## 11. Implementation tasks (dependency order)

1. ADR and format specification; owner decisions D-1 to D-3.
2. Migrations (0005 onward): `client_import`, `imported_audit_event`, external-principal
   marker and guards, D-1 index drop; tests for each guard; harness migration list.
3. Domain transfer types, canonical JSON writer, manifest/checksum model; unit tests.
4. Exporter: snapshot read, whitelisted boundary selection, package writer,
   `CLIENT_EXPORTED`, authorization. Tests prove no foreign client rows or events, no email,
   no system tables.
5. Reader and validator with no writes: container safety, checksums, compatibility,
   reference closure, manifest recomputation, conflict detection. Tests with corrupted and
   tampered packages expecting the matching codes.
6. Import writer in section 5 order: principals, recipient memberships,
   finalize-after-insert, archived history, `client_import`, post-write re-verification,
   `CLIENT_IMPORTED`. Tests for rollback on each failure, `VerifyChainAsync` still true,
   digests unchanged.
7. End-to-end round trip across two workspaces using the demo data: export from A, import into
   B, compare comparatives, create and finalize FY2028 in B, check `prior_root_digest` links to
   the imported year.
8. Python harness parity: reference verifier for .NET-produced packages and the new event
   vocabulary.
9. UI pages for export and import with the preview report.
10. Docs: README, `awb-package-design.md`, security model; mark
    `IAuditEngagementPackageService` as superseded.
