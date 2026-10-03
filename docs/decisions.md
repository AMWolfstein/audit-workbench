# Architecture Decisions

This log records decisions before implementation. Statuses are **Accepted**, **Proposed**, or **Deferred**. A changed decision should be superseded by a dated entry rather than silently rewritten once implementation begins.

## ADR-001: Financial year is an engagement boundary

- **Status:** Accepted
- **Decision:** Model each company/financial-year audit as a newly inserted Engagement with a stable ID. All audit data is directly engagement-owned.
- **Reason:** Preserves historical audit files, permits independent current-year work, and makes isolation enforceable.
- **Consequences:** More rows and explicit comparisons/roll-forward; no “change year” operation; uniqueness and ownership rules are central.
- **Rejected:** One mutable company audit record whose year field is updated.

## ADR-002: Finalized is terminal in MVP

- **Status:** Accepted
- **Decision:** `DRAFT -> FINALIZED` is the only completion transition. No normal unfinalize/edit/delete exists.
- **Reason:** A reversible flag is too easy to misuse and does not satisfy historical preservation.
- **Consequences:** Corrections after finalization require a future governed supersession/exception design. Application guards, DB triggers, manifest/digest, and tests are required.

## ADR-003: Explicit prior-year relationship

- **Status:** Accepted
- **Decision:** Store a one-way record from current engagement to one finalized, earlier engagement of the same company.
- **Reason:** Fiscal calendars can have gaps/changes; guessing “year - 1” is unreliable and unauditable.
- **Consequences:** Eligibility validation and lineage UI are required. Relation is immutable in MVP.

## ADR-004: Portable modular monolith on .NET 8 LTS

- **Status:** Accepted
- **Decision:** Self-contained ASP.NET Core modular monolith, published as a versioned ZIP.
- **Reason:** Works without admin/runtime/service/internet, offers mature security and SQLite support, and avoids distributed-system overhead.
- **Consequences:** Larger package than framework-dependent deployment; target runtime patches must be shipped with app updates. Single local process is intentional.

## ADR-005: Server-rendered localhost UI

- **Status:** Accepted
- **Decision:** Razor Pages served by Kestrel on loopback only; small JavaScript enhancements, no deployed Node runtime/CDN.
- **Reason:** Forms/reporting application needs accessibility and low operational complexity more than a heavy SPA shell.
- **Consequences:** Must secure browser requests despite localhost, manage startup/browser launch/session, and test browser policy. Rich offline client interactions can be added selectively.

## ADR-006: SQLite per workspace

- **Status:** Accepted
- **Decision:** SQLite is the embedded transactional store for one local active user/process.
- **Reason:** Portable, no service/admin install, robust transactions and constraints, good .NET tooling.
- **Consequences:** Not a supported network-share or concurrent multi-user database. Enable FKs, safe journaling/backup, single-instance lock, integrity checks, explicit migrations, and tested recovery.
- **Rejected:** SQL Server/PostgreSQL for the local edition.

## ADR-007: Layered enforcement of immutability

- **Status:** Accepted
- **Decision:** Enforce finalized protection at UI, application policy, repository ownership, SQLite triggers, and canonical digest verification.
- **Reason:** Any one layer can be bypassed accidentally; UI disablement is not integrity.
- **Consequences:** Every new engagement-owned table must register guard and manifest behavior. Filesystem owners remain a residual threat clearly documented.

## ADR-008: Append-only financial revisions and audit events

- **Status:** Accepted
- **Decision:** A draft correction inserts a revision; events append in the business transaction. Application business records have no silent hard delete.
- **Reason:** Auditability needs both state history and action history.
- **Consequences:** Queries resolve latest revisions, storage grows, and retention/archive policy is needed later. Audit metadata avoids sensitive payload duplication.

## ADR-009: Exact monetary representation

- **Status:** Accepted in principle; physical representation to validate
- **Decision:** Never use binary floating point. Prefer signed 64-bit minor units plus currency/scale for MVP; validate maximum audit-office values before migration 001.
- **Reason:** Deterministic accounting arithmetic and comparison.
- **Consequences:** Overflow/range and currency-scale handling require tests. If requirements exceed 64-bit, use canonical decimal text or another tested exact mapping—not SQLite `REAL`.

## ADR-010: Roll-forward copies; comparison references

- **Status:** Accepted
- **Decision:** Comparison reads finalized prior records through the relationship. Future roll-forward creates new current-owned rows with provenance.
- **Reason:** Shared mutable records would violate year isolation.
- **Consequences:** Need matching rules, new IDs, source/version metadata, duplicate prevention, and current-year reassessment.

## ADR-011: Application-managed backup and staged restore

- **Status:** Accepted
- **Decision:** Use SQLite backup-consistent snapshots plus workspace manifests/checksums and attachments; restore to staging and verify before activation.
- **Reason:** Copying an open DB/attachments ad hoc can be inconsistent and untested backups give false assurance.
- **Consequences:** Backup format/version and encryption destination are product/operations concerns; restore drills are release criteria.

## ADR-012: Synthetic data only

- **Status:** Accepted
- **Decision:** Tests, fixtures, documentation, demos, screenshots, and examples use generated, visibly synthetic organizations and values.
- **Reason:** Audit data is confidential and source/history leakage is unacceptable.
- **Consequences:** Maintain data generators/scanners and never derive fixtures from production, even if “anonymized.”

## ADR-013: Authentication abstraction first, approved local identity before confidential use

- **Status:** Accepted
- **Decision:** Domain/application depend on `CurrentActor` and permission policies. A clearly unsafe demo actor may support early engineering only. Release hardening uses Windows identity if approved or maintained local password authentication.
- **Reason:** Avoid premature identity complexity without baking a fake user into business records.
- **Consequences:** Authentication choice requires corporate-policy validation; attribution and authorization exist from the beginning.

## ADR-014: Managed future attachments outside SQLite

- **Status:** Proposed
- **Decision:** Store large binaries in a managed, non-web-root, content-addressed directory; store metadata/hash/version/engagement ownership in SQLite and include both in backup/finalization.
- **Reason:** Keeps database manageable while preserving integrity and lifecycle linkage.
- **Consequences:** Atomic file/database operations, orphan cleanup, encryption, malware handling, and restore need careful design. Revisit before working papers.

## ADR-015: Baseline at-rest protection uses corporate device controls

- **Status:** Proposed pending IT validation
- **Decision:** Require OS account/ACL and corporate full-disk encryption; approved encrypted backup destination. Do not claim baseline SQLite is encrypted.
- **Reason:** Portable embedded key management is difficult and a bundled key defeats encryption.
- **Consequences:** Deployment readiness depends on IT confirmation. Evaluate SQLCipher/envelope encryption separately if policy requires database-level encryption.

## ADR-016: No microservices or generic workflow engine

- **Status:** Accepted
- **Decision:** Keep explicit use cases and module boundaries in one deployable process; implement only lifecycle states currently needed.
- **Reason:** Local single-user constraints and cross-module finalization benefit from simple transactions. A generic engine would obscure core invariants.
- **Consequences:** Future shared edition may separate infrastructure, while retaining domain contracts.

## ADR-017: Canonical finalization manifest `AWB-MANIFEST/1.0`

- **Status:** Accepted (resolves the deferred "canonical manifest format" decision)
- **Decision:** Finalization computes a SHA-256 digest over a line-oriented, UTF-8, LF-terminated
  canonical document described in [finalization-manifest.md](finalization-manifest.md): a version
  header, engagement identity fields, then one `account|<code>|<name>|<type>|<revision>|<amount>`
  line per account sorted by account code (ordinal), then `END`. Separators inside values are
  escaped (`\` then `|` then CR/LF). The digest is stored on both the manifest row and the
  engagement, and can be recomputed from live data at any time.
- **Reason:** A finalized year must be provably unchanged. A canonical text form is auditable by a
  human, is stable across runtimes and library versions, and avoids depending on a JSON
  canonicalization standard that SQLite and .NET would have to agree on. Amounts are serialized as
  integer minor units, so no floating-point formatting can perturb the digest.
- **Consequences:** The grammar is a frozen contract: `tests/fixtures/manifest_v1_example.txt` and
  its `.sha256` are asserted from both the C# tests and the Python verification harness. Any change
  to the grammar requires a new version token (`AWB-MANIFEST/1.1`) and a migration of stored
  digests; it may never be edited in place.

## ADR-018: Immutability is enforced by the database, not only by services

- **Status:** Accepted
- **Decision:** Every year-isolation and finalization invariant is expressed in SQL: `STRICT`
  tables, `CHECK` constraints, a composite `(account_id, engagement_id)` foreign key that makes
  cross-year values unrepresentable, and `BEFORE` triggers that `RAISE(ABORT, 'AWB-GUARD-<AREA>: …')`.
  The application layer repeats the checks for good error messages, and
  `SqliteErrorTranslator` maps a raised guard back onto the matching domain exception.
- **Reason:** The workspace file is a user-writable SQLite database; the operator can open it with
  any SQLite tool. A rule that lives only in C# is advisory. Triggers also protect against
  application bugs, partially applied transactions and future code paths.
- **Consequences:** Guard names are a stable vocabulary shared by SQL, C# and the tests. Because
  SQLite does not define the firing order of triggers on the same event, overlapping guards must be
  made mutually exclusive through their `WHEN` clauses rather than by relying on order.

## ADR-019: Hash-chained audit trail with an independent verification harness

- **Status:** Accepted
- **Decision:** `audit_event` is append-only (enforced by trigger) and each row stores
  `event_hash = SHA-256(sequence_no | event_id | occurred_at | actor | type | outcome | company |
  engagement | entity_type | entity_id | description | details_json | previous_hash)`, so the trail
  is a chain verifiable in one pass. Alongside the .NET solution, `tools/verification/` runs the
  **real** migration and query SQL against SQLite from Python and asserts the same invariants
  (year isolation, finalization, comparatives, audit chain, backup, manifest fixture).
- **Reason:** The audit trail is the evidence that a finalized year was never altered; a plain log
  table proves nothing if rows can be edited. The second harness exists because the SQL layer is
  where the guarantees live: it exercises the schema without a .NET toolchain, catches drift
  between the shipped SQL and the documented behaviour, and gives a reviewer a runnable proof.
- **Consequences:** Two implementations of the manifest and digest rules must stay in step; the
  frozen fixture is what keeps them honest. The harness is a development tool, is not part of the
  distributable package, and depends on the Python standard library only.

## Deferred decisions / required discovery

| Decision | Needed before | Questions |
|---|---|---|
| Windows versions/CPU targets | Packaging implementation | x64 only? ARM? approved browser/WebView behavior? |
| Identity option | MVP hardening | Windows integrated identity permitted? local account recovery owner? MFA expectation? |
| Local HTTPS | Security release review | Can a trusted cert be provisioned without admin? Is HTTP loopback accepted? |
| Database-level encryption | Confidential pilot | Is BitLocker mandatory/verified? SQLCipher licensing and key recovery acceptable? |
| Currency/scale/sign convention | Migration 001 | Single/multi-currency? units/decimals? max balances? debit/credit display? |
| Fiscal periods | Engagement implementation | 52/53-week years, changed year-end, periods over 12 months? |
| Finalization authority | Finalization implementation | Which role(s), dual approval, required checklist/backup? |
| ~~Canonical manifest format~~ | Resolved by ADR-017 | `AWB-MANIFEST/1.0`; new grammar requires a new version token |
| Backup destination/retention | MVP hardening | Corporate approved path/media, encryption tool, generations, recovery custody? |
| Legal retention/reopening | Post-MVP governance | Jurisdictional periods, legal hold, correction/supersession model? |
| Expected volumes | Performance test plan | Companies, years, TB rows, attachment sizes, backup window? |
| Export libraries | Export phase | Office fidelity, macro/formula controls, PDF archival needs, licenses? |
| Shared/multi-user edition | Future roadmap | Is it needed, and what identity/server/database constraints apply? |

## Decision quality gates for future modules

Before adding a year-specific module, document:

1. engagement ownership and cross-year behavior;
2. draft versioning and deletion/correction semantics;
3. finalization preflight, trigger, and manifest coverage;
4. audit event vocabulary and sensitive-data minimization;
5. authorization policies;
6. backup/restore and migration impact;
7. import/file threat handling where applicable; and
8. synthetic test fixtures and isolation tests.

A feature that cannot answer these questions should not enter production implementation.

## ADR-020: Central team database; SQLite is non-production

- **Status:** Accepted; supersedes ADR-006 and the single-user parts of ADR-003/ADR-016.
- **Decision:** Live team engagements use an Application/API and central relational database. PostgreSQL is preferred; SQL Server remains compatible through provider-specific infrastructure. SQLite remains for local development, automated integration tests, and the integrity harness.
- **Consequences:** `AuditDatabaseOptions` selects providers. SQLite SQL is no longer the production schema definition. Provider-native migrations/guards and execution tests are required before central deployment.

## ADR-021: Explicit engagement membership and data-driven permissions

- **Status:** Accepted.
- **Decision:** An active `User` gains engagement access only through an active `EngagementMember` whose `Role` has the required `RolePermission`. Initial named roles/mappings are seeded data, not immutable domain rules. Assignments are generic engagement-owned references until audit modules exist.
- **Consequences:** All engagement queries and commands are deny-by-default. Platform administration does not imply client access. Membership history is retained and finalized with the year.

## ADR-022: Optimistic concurrency

- **Status:** Accepted.
- **Decision:** Mutable aggregates use explicit numeric `row_version` concurrency tokens and expected-version commands. Stale writes return `AWB-CONCURRENCY`; silent overwrite and automatic business-conflict retry are forbidden.
- **Consequences:** UI/API must return a clear conflict and support reload/reapply. Provider tests must prove zero-row stale updates. Independent rows remain concurrently editable.

## ADR-023: Evidence storage and `.awb` are separate ports

- **Status:** Accepted foundation; formats/providers deferred.
- **Decision:** Evidence metadata belongs in the relational database while bytes use `IFileStorage`. `.awb` import/export uses `IAuditEngagementPackageService` and represents one portable point-in-time engagement package, never a live database.
- **Consequences:** Existing SQLite `AWB-BACKUP/1.0` is retained for MVP compatibility but is not the future `.awb` format. Hashes, consistent snapshots, path safety, encryption/signing, and import collision semantics require dedicated implementation/review.

## ADR-024: Workspace-level privilege for user administration, backup and workspace events

- **Status:** Accepted.
- **Decision:** Actions that are not owned by one engagement (creating, deactivating or reactivating users; creating a backup, which contains every engagement; reading audit events that have no engagement) require *workspace privilege*: an authenticated, active user holding an active `PARTNER` or `MANAGER` membership on at least one engagement. A workspace with no engagement at all is unowned and open to any authenticated actor so that it can be bootstrapped; the first engagement creator becomes Partner (ADR-021). No schema change: the rule derives from existing membership and role rows. Engagement-scoped audit reads require `VIEW_AUDIT_TRAIL` on that engagement, and unscoped reads only return events of engagements where the actor holds it.
- **Consequences:** Non-members and members without Partner/Manager cannot export data or administer accounts. The rule is intentionally coarse and should become a dedicated workspace-administrator permission when central identity is added. `VerifyChainAsync` and `CountAsync` stay unguarded because they expose only a boolean and a count.

## ADR-025: Refused protected writes are audited as REJECTED events

- **Status:** Accepted.
- **Decision:** When a command fails with an authorization refusal or a write against a finalized engagement (application guard or database trigger), `UnitOfWork` rolls the command back and then `RejectionAuditor` appends one `PROTECTED_WRITE_REJECTED` event with outcome `REJECTED` in its own transaction through the normal `AuditTrailWriter`, so `sequence_no` and `previous_event_hash` continue the committed chain. Details hold only the error code and a reason class, never amounts, input or exception text. Recording is best effort and never replaces the original error. No schema change: the `outcome` check and event vocabulary already allowed it, and the Python harness already uses the same canonical hash.
- **Consequences:** Read-only denials and validation errors are not recorded, to avoid flooding the append-only trail. An actor with no `app_user` row cannot be recorded (actor foreign key). Repeated rejections are visible to engagement members holding `VIEW_AUDIT_TRAIL`; rate limiting remains future work.

## Superseded/deferred entries

The earlier “shared/multi-user edition” deferred decision is resolved by ADR-020. Identity mechanism remains deferred, but authentication is now mandatory for production and must populate `ICurrentActor` server-side. The local actor is development-only.
