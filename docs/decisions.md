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
| Canonical manifest format | Finalization implementation | JSON canonicalization or table digest rules; algorithm agility/versioning? |
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
