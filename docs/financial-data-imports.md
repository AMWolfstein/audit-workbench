# Trial Balance and General Ledger Imports

This document describes the financial data foundation added after the Foundation
contract: the financial period every dataset belongs to, the trial-balance (TB)
and general-ledger (GL) import flows, dataset versioning, transaction identity,
the TB ⇄ GL reconciliation, the snapshot comparison that later roll-forward work
depends on, and the security and audit behavior of both flows. Schema:
`db/migrations/0007_financial_data_foundation.sql`. Read side:
`db/sql/*.sql`. Application: `src/AuditWorkbench.Application/FinancialData`.

## 1. The chain: client → engagement → financial period → dataset

Nothing is imported into an engagement that has no financial period. A period is
created with the engagement (in the same transaction) and carries the fiscal
year, the period start and end taken from the financial year, an explicit
reporting date and a status.

| Period status | Meaning | Imports |
|---|---|---|
| `OPEN` | The year is being worked on | allowed |
| `IN_PROGRESS` | Fieldwork underway | allowed |
| `FINALIZED` | The engagement's reporting is agreed | refused |
| `LOCKED` | Sealed, permanently read-only | refused |

A period is never re-used across engagements, an engagement has exactly one
period, and the reporting date must fall inside the period. Database triggers
(not application code alone) enforce the parts that would otherwise be forgeable:
a period cannot be deleted, its identity columns cannot be rewritten, the status
may only move forward, a locked period is never reopened
(`AWB-GUARD-PERIOD-TRANSITION`), and the reporting date of a finalized or locked
period is sealed (`AWB-GUARD-PERIOD-SEALED`).

**2027 never touches 2026.** A new year is a new engagement with its own
financial year and its own period; every TB row and every GL journal and line
stores its `financial_period_id`, and triggers refuse a row written into a
period that is not open for imports. There is no code path that moves, rewrites
or deletes rows belonging to another year.

## 2. Upload: never trust the file

The upload step validates the file type and size, sniffs the real format from the
bytes (never from the file name), computes the SHA-256 digest, stores the bytes
in the managed attachment store and records the upload row with its detected
structure.

* accepted: comma/semicolon/tab/pipe delimited `.csv`/`.txt`, and `.xlsx`;
* refused: legacy binary `.xls`, PDFs, archives, anything not recognizable as a
  tabular extract, files above 64 MB, files with more than 256 columns,
  more than 5,000,000 rows, or a single cell above 2,000 characters;
* the reader streams: a 350,000-line ledger is read row by row and the import
  never holds the dataset in memory as a whole;
* an uploaded file is never executed, never interpreted as a path, and never
  unpacked outside the managed store.

## 3. Trial balance flow

1. **Detect structure** - find the header row inside the preview (client files
   carry titles, company names and blank rows above it), list the columns, count
   the extra client columns that will be preserved without a schema change.
2. **Preview and map** - the preview shows the first rows as read; the client's
   column names are matched against the internal TB fields by alias
   (`Account code`, `Account name`, `Debit`, `Credit`, `Balance`, `Currency`,
   `Cost center`, `Account group`). The mapping is stored as canonical JSON on
   the import, so the import can be repeated exactly.
3. **Validate** - a full streaming pass that reports per-row findings and totals.
4. **Import** - the same file is streamed again *inside* the transaction that
   writes the rows; a file that changed on disk since validation is refused.
5. **Reconcile** - TB against the GL snapshot (section 6).
6. **Finalize** - the TB version is marked final; later correction means a new
   version.

Blocking (`ERROR`) findings: a required column that is unmapped, missing or
malformed values, duplicate account codes (including duplicates that differ only
by separators or case), a missing account name, unsupported currencies, and an
unbalanced trial balance. Warnings (`WARNING`) are recorded and visible: an account
without a name, and a balance that does not equal debit minus credit.
Informational findings (`INFO`) are kept as context: a totals row that was
skipped, an account that is new to the master, an account whose name differs
from the master, a row that carries no amount at all.

An unbalanced trial balance is **never** imported silently: it is an error until
the operator confirms the difference explicitly (`allowUnbalanced`), after which
the import records `UNBALANCED_TRIAL_BALANCE` as a warning, stores the
difference, and the import's validation status is `VALID_WITH_WARNINGS`. The
trial balance screen reports `TB Balanced: YES/NO` from the same totals.

Import is transactional: the rows, the import header, the account-master
additions and the audit events are written in one transaction. A catastrophic
failure leaves the workspace without a partially populated dataset.

## 4. General ledger flow

The GL carries engagement, period and (where the account is in the trial
balance) account context. Internal fields: journal number, journal source,
transaction date, posting date, account code, account name, description, debit,
credit, signed amount, currency, reference, line number, prepared by - mapped
from client names such as `Journal No`, `Document No`, `Voucher No`, `Entry ID`
(journal identity), `Reference`, `Line No` (line identity).

Validation adds GL-specific rules to the TB rules: a transaction date that
cannot be read, a posting date that cannot be read, a missing journal identity
(the identity is then derived deterministically and the row is flagged), a
duplicate transaction identity within the import, numeric failures, and account
codes that are not in the trial balance (a warning that points to the
reconciliation, never a silent drop).

**Period control.** A transaction whose date falls outside the financial period
is imported and flagged (`TRANSACTION_OUTSIDE_PERIOD`, counted in
`out_of_period_count`), never moved into the period, never deleted, never
ignored. `db/sql/gl_out_of_period.sql` returns the exact list with the source row
number so a finding can be traced back into the client's file.

Out-of-period rows are not a blocking error: the auditor decides what to do with
them, and the reconciliation counts them per account.

## 5. Versioning and provenance

One import = one *version* = one snapshot of a dataset at a point in time.

| Import status | Meaning |
|---|---|
| `DRAFT` | rows written, not yet the active version |
| `VALIDATED` | validation recorded, nothing committed |
| `IMPORTED` | committed and active |
| `FINALIZED` | agreed; corrections require a new version |
| `SUPERSEDED` | a newer version replaced it; still readable |

* exactly one version per dataset kind and engagement is active, enforced by a
  partial unique index;
* activating version *n+1* supersedes version *n* in the same transaction, and
  the earlier row records `superseded_by_import_id`;
* previous versions are **never** deleted or rewritten: triggers refuse deletes
  and refuse changing an import's identity or its statistics
  (`AWB-GUARD-DATASET-*`);
* an imported row is append-only (`AWB-GUARD-TB-APPEND-ONLY`,
  `AWB-GUARD-GL-APPEND-ONLY`): a correction is a new version, not an edit;
* every import keeps the source file name, SHA-256, size, sheet name, header row
  number, the mapping JSON, the validation report, row counts, totals and the
  actor and timestamp of the import.

**Idempotency.** Uploading the same bytes again is recognized: the upload
reports the earlier imports of the same digest, and importing it again requires
an explicit `allowRepeat`, which records the new version as a repeat of the
earlier one. The distinction between "same file, on purpose" (new version) and
"same file, by accident" (refused) is therefore always visible.

## 6. Transaction identity (the client's identity, not the database's)

A database row id is an implementation detail. Every GL journal and line carries
a **client/source identity** that survives re-imports and comparison:

* **journal identity** - the client's journal/document/voucher number
  (`SRC:<canonical key>`), or, when the file has none, a deterministic composite
  of the journal's attributes plus its ordinal among identical journals;
* **line identity** - the client's line number/entry id, or a deterministic
  composite when absent;
* **value hash** - a digest over the monetary values, and **line hash** - a
  digest over the identity, the descriptive attributes (account, date,
  description, reference, source) and the value hash. Together they let a later
  comparison say *what kind* of change happened without guessing: a value edit
  moves both digests, an attribute-only edit moves the line digest alone;
* the source row number and the source line number are preserved beside the
  identity, so any finding can be traced back into the client's file.

`identity_source` records whether the identity was supplied (`SOURCE`) or derived
(`DERIVED`); a derived identity is flagged in the validation report because it is
weaker evidence.

## 7. TB ⇄ GL reconciliation

One trial-balance version against one ledger snapshot, account by account, on one
**sign convention**:

> All amounts are debit-positive: `balance = debit − credit` and
> `gl net = sum(debit) − sum(credit)`. A credit balance is negative.

| Status | Meaning |
|---|---|
| `MATCHED` | TB balance equals the GL net |
| `DIFFERENCE` | both sides present, amounts differ (the difference is shown) |
| `TB_WITHOUT_GL` | the account is in the trial balance, with no ledger activity |
| `GL_WITHOUT_TB` | the ledger moved on an account that is not in the trial balance |

An account present in only one dataset is returned with empty values on the
other side rather than being dropped, so the exception list is complete. The
summary reports totals for both sides, the overall difference, the number of
out-of-period ledger lines and a machine-checkable `IsFullyReconciled`. A
difference is never presented as agreement.

## 8. Snapshots and roll-forward

Periods are audited in stages: the ledger file received in May is not the ledger
file received in December. Each import is therefore kept as its own snapshot
(snapshot #1 through 2026-05-31, snapshot #2 through 2026-12-31, ...).

`db/sql/gl_snapshot_comparison_summary.sql` and
`.../gl_snapshot_comparison_detail.sql` compare two snapshots of the same period
by transaction identity - never by database id - and classify every transaction:

| State | Meaning |
|---|---|
| `UNCHANGED` | same identity, same attribute and value digests |
| `CHANGED_VALUE` | same identity, different amounts (the delta is reported as current − previous net) |
| `CHANGED_ATTRIBUTES` | same identity and amounts, different account/date/description/reference |
| `ADDED` | present only in the new snapshot |
| `REMOVED` | present in the examined snapshot, missing from the new one |

The summary also reports the debit/credit totals of added and removed lines and
the net movement of changed values. **This phase only establishes the
comparison**: no sampling logic and no selection rules live here. A later phase
(roll-forward / sampling) consumes `ADDED`, `CHANGED_VALUE` and
`CHANGED_ATTRIBUTES` as the population of transactions eligible for mandatory
testing. Snapshots are compared oldest-first and only within one period; the
comparison refuses imports of another engagement or of a different period.

The same idea applies to the trial balance through
`db/sql/tb_version_comparison.sql`, which compares two TB versions account by
account with the same vocabulary (`ADDED`, `REMOVED`, `UNCHANGED`,
`CHANGED_VALUE`, `CHANGED_ATTRIBUTES`).

## 9. Account master and audit areas

Accounts are owned by the engagement: uniqueness is scoped to
`(engagement_id, account_code)`, never global. Alongside the client's code the
master keeps a **normalized code** (upper case, separators removed) that is used
for matching, so `1000`, `10-00` and `10.00` are the same account. An account's
**origin** records how it appeared - `MANUAL`, `TB` or `GL` - and is immutable
provenance.

An account may be linked to an **audit area** of the same engagement, for
example Revenue, Receivables, Inventory, Purchases, Payables, Cash, Payroll or
Fixed Assets. Areas are engagement-owned rows (`audit_area`), named by the team;
nothing is seeded and the link is optional. This is an extension point only:
importing a trial balance never requires a classification, and no classification
engine exists in this phase.

## 10. Security and audit trail

* every operation goes through the same authorization service as the rest of the
  application; engagement and period ids supplied by a client are always
  re-verified server-side, and an import id from another engagement is simply
  not found;
* an actor is never taken from the request: the recorded actor comes from
  `ICurrentActor`;
* the audit trail records `FINANCIAL_UPLOAD_RECEIVED`, `TB_IMPORT_STARTED`,
  `TB_IMPORT_COMPLETED`, `TB_IMPORT_FAILED`, `GL_IMPORT_STARTED`,
  `GL_IMPORT_COMPLETED`, `GL_IMPORT_FAILED`, `DATASET_ACTIVATED`,
  `DATASET_SUPERSEDED`, `TB_FINALIZED`, `GL_FINALIZED` and the period events;
* an event carries counts, digests, labels and the blocking findings - never the
  imported rows. A refused import is audited in its own transaction, so the
  attempt survives the abandoned import;
* the audit trail itself remains append-only.

## 11. Performance notes

* imports stream the file twice (validate, then write inside the transaction) and
  never materialize a dataset in memory;
* rows are written with multi-row `INSERT` statements sized to the provider's
  parameter limit, inside the caller's single transaction;
* the read side pages in SQL and returns a total match count with the page;
* indexes exist only where the queries need them: period + account code,
  period + transaction identity, period + transaction date, import id, and
  engagement/period.

## 12. Deliberately out of scope in this phase

Sampling (including roll-forward selection), automated account classification,
materiality formulas (the materiality record is stored, not computed), period
extension analysis beyond the snapshot comparison, and any analytics engine.
Corrections to imported or finalized data are always new versions, never edits.
