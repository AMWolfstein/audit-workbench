# Requirements

## 1. Purpose

Audit Workbench will manage audit engagements on a single corporate laptop while preserving each completed audit year as an independent historical record. This specification separates the small proof-oriented MVP from the intended product direction.

Normative terms **MUST**, **SHOULD**, and **MAY** indicate required, recommended, and optional behavior.

## 2. Domain definitions

- **Company:** relatively stable client master data, such as legal name and an internal reference. It is not an audit file.
- **Financial year:** a named reporting period with start and end dates (for example, FY2026, 1 January–31 December 2026).
- **Engagement:** the audit-file boundary for one company and one financial year. Financial data and, later, risks, materiality, procedures, evidence, conclusions, and sign-offs belong to it.
- **Prior-year relationship:** an explicit, immutable link from a current engagement to an older engagement for the same company.
- **Finalization:** an atomic transition after validation that locks the engagement and its owned records against ordinary creation, update, or deletion.
- **Roll-forward:** creation of new current-year records from selected prior-year structures, with source provenance. It is a copy operation, not shared mutable state.
- **Normal editing:** actions available in the standard application workflow. Filesystem/database administrators remain outside the application's absolute trust boundary; tamper evidence and device controls mitigate that risk.

## 3. Stakeholders and actors

| Actor | Need |
|---|---|
| Preparer | Create and update draft engagement data; view prior-year comparisons |
| Reviewer | Inspect work, eventually raise/clear review notes, and approve stages |
| Engagement manager/partner | Control finalization and future exceptional administrative actions |
| Local workspace custodian | Back up, restore, and validate a workspace without seeing cloud dependencies |
| Security/IT | Confirm local binding, controlled storage, integrity, supportability, and no elevated installation |

For MVP these responsibilities may be exercised by one local user, but operations still carry an actor identity and the design preserves role boundaries.

## 4. Core invariants

1. Every year-specific record **MUST** carry a non-null `engagement_id` (directly, not inferred only from UI context).
2. An engagement **MUST** belong to exactly one company and exactly one financial year.
3. A company **MUST NOT** have two active engagements for the same financial period unless a future, explicit multi-engagement type is designed.
4. Changing a year on an existing audit record is prohibited; a new year means a new engagement.
5. A prior-year relationship **MUST** point to a different, earlier, finalized engagement of the same company.
6. Finalization **MUST** be transactional: either all lock metadata and audit evidence are committed or none are.
7. After finalization, normal operations **MUST NOT** insert, update, or delete any engagement-owned business data.
8. Current-year calculations **MUST** read prior values through the explicit relationship and **MUST NOT** update them.
9. Engagements and audit events **MUST NOT** be hard-deleted through the application.
10. Every accepted business mutation **MUST** generate an audit event in the same transaction.
11. Development, demonstrations, automated tests, screenshots, and fixtures **MUST** use synthetic data only.

## 5. MVP functional requirements

### Company and engagement

- **FR-M01:** Create a company with generated ID, legal name, internal reference, timestamps, and creator.
- **FR-M02:** List and view a company's engagements without combining their year-owned data.
- **FR-M03:** Create a draft engagement with label, start date, end date, company, and creator.
- **FR-M04:** Reject invalid periods, duplicate company/period engagements, and attempts to “advance” an existing engagement by changing its year.
- **FR-M05:** Optionally select one eligible finalized engagement of the same company as prior year when creating a later engagement.

### Basic financial data

- **FR-M06:** Define/use a minimal year-specific account set and enter monetary values for an engagement.
- **FR-M07:** Preserve revisions in draft rather than silently replacing history.
- **FR-M08:** Display latest current-year values and, when linked, latest finalized prior-year values side by side.
- **FR-M09:** Calculate absolute and percentage change deterministically. If the prior amount is zero, percentage is `N/A` rather than divide-by-zero or an invented percentage.
- **FR-M10:** Store monetary values as exact scaled integers (minor units) or fixed-precision decimal—not floating point.

### Finalization and protection

- **FR-M11:** Run preflight validation and show blocking errors before finalization.
- **FR-M12:** Require an explicit confirmation naming the company and financial year.
- **FR-M13:** In one database transaction, record lock metadata, finalization manifest/digest, and audit event, then transition `DRAFT` to `FINALIZED`.
- **FR-M14:** Reject all ordinary writes to the finalized engagement at application and database levels.
- **FR-M15:** Keep finalized engagement data readable for display and comparison.
- **FR-M16:** Do not expose reopen/unfinalize or delete controls in MVP.

### Auditability and local operations

- **FR-M17:** Record create, revise, finalize, link, backup, restore, login/logout (when authentication is enabled), and rejected protected-write attempts as appropriate audit events.
- **FR-M18:** Show who/when for engagement creation and finalization.
- **FR-M19:** Allow an explicit consistent backup and a validated restore workflow by MVP completion.
- **FR-M20:** Operate without internet connectivity.

## 6. Post-MVP product requirements

These shape extensibility but are not implementation commitments in the MVP:

- trial-balance import with import batches, validation, source hashes, and reconciliation;
- account mapping and financial-statement categories;
- materiality calculations and revisions;
- risk assessment linked to assertions, accounts, responses, and procedures;
- audit planning, programs, procedures, sampling, conclusions, and sign-offs;
- working papers and attachments with versions, references, and review notes;
- findings and management responses;
- controlled sign-off and final completion checks;
- selective roll-forward with conflict resolution and provenance;
- Excel/PDF/Word exports whose metadata identifies engagement, generation time, and version;
- retention, legal hold, controlled exceptional reopening/supersession, and archive packages.

Every future year-specific module must use `engagement_id` ownership and participate in finalization guards.

## 7. Non-functional requirements

### Portability and compatibility

- **NFR-01:** Release must run from a user-accessible directory with no administrator rights, installer, Windows service, Docker, database server, or separately installed runtime.
- **NFR-02:** Bind HTTP only to `127.0.0.1` and/or `::1`; never all network interfaces by default.
- **NFR-03:** Use relative browser URLs; no cloud API or internet dependency is mandatory.
- **NFR-04:** Keep application binaries/configuration separate from mutable workspace data so upgrades do not overwrite data.
- **NFR-05:** Detect unsupported schema/application versions and refuse unsafe opening rather than guessing.

### Integrity and reliability

- **NFR-06:** Enable SQLite foreign keys, transactions, busy timeout, and integrity checks; use WAL only with backup procedures compatible with it.
- **NFR-07:** Database migrations must be ordered, checksummed/tested, and backed up before execution.
- **NFR-08:** Unexpected shutdown must not leave an engagement half-finalized.
- **NFR-09:** Backup must capture a transactionally consistent database and managed attachments; copying an open `.db` file manually is not the supported process.
- **NFR-10:** Restore must verify manifest, checksums, schema compatibility, and database integrity before replacing the active workspace.

### Security and privacy

- **NFR-11:** Follow least privilege and deny-by-default authorization at application-service boundaries.
- **NFR-12:** Passwords, if used, must be salted and hashed using a maintained adaptive password hasher; plaintext/reversible passwords are forbidden.
- **NFR-13:** Secrets and production data must not enter source control, logs, demo fixtures, or crash reports.
- **NFR-14:** Sensitive logs must avoid financial values and document contents; audit metadata should identify records, not reproduce evidence.
- **NFR-15:** Depend on corporate full-disk encryption and access controls for baseline at-rest protection; document residual risks and consider encrypted workspace support later.

### Usability, accessibility, and performance

- **NFR-16:** Clearly display company, current year, state, and prior-year source on every engagement workspace page to reduce cross-year mistakes.
- **NFR-17:** Finalized state must be unmistakable and all editing affordances disabled, while server-side rejection remains authoritative.
- **NFR-18:** Target keyboard access and WCAG 2.2 AA for core workflows.
- **NFR-19:** For the intended single-user local dataset, common lists and comparison views should respond within one second on a supported corporate laptop; measured thresholds will be set after a data-volume study.

## 8. MVP acceptance scenario

Using synthetic **ABC Manufacturing (Demo)**:

1. Create FY2026 and enter Revenue `850,000,000`, Receivables `180,000,000`, Inventory `240,000,000`.
2. Finalize FY2026. Record status, actor, UTC timestamp, manifest/digest, and event.
3. Verify UI and direct repository/service attempts to revise, append, or delete FY2026 data fail.
4. Create FY2027 as a new engagement linked to FY2026.
5. Enter Revenue `920,000,000`, Receivables `210,000,000`, Inventory `275,000,000`.
6. Display both years and changes. Revenue change is `70,000,000` and `+8.24%` (rounded for display from `70,000,000 / 850,000,000 × 100`).
7. Verify FY2026 row counts, values, revision history, digest, and finalization metadata are unchanged.
8. Restart offline and repeat the comparison successfully.

## 9. Assumptions and open validation items

- Initial target is a single Windows laptop and one active process per workspace.
- Currency and rounding policy, fiscal-period edge cases, expected data volumes, retention periods, approved backup destinations, and corporate endpoint-encryption controls require stakeholder confirmation.
- Multi-user concurrent network access is not supported by the selected SQLite/local architecture. A future shared deployment would retain domain boundaries but require a server database and centralized identity.
- Legal/regulatory definitions of “finalized,” exceptional reopening, electronic sign-off, retention, and audit-evidence admissibility require governance approval before those features are represented as compliant.
