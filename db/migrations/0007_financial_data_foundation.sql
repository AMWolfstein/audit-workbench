-- Audit Workbench migration 0007 - TB/GL import and financial data foundation
--
-- Adds the financial-data layer that sits between the engagement boundary and
-- the future audit procedures:
--
--   financial_period        period profile (reporting date, period lock state)
--   financial_upload        preserved source file of an operator upload
--   dataset_import          one TB import version / one GL snapshot (versioned)
--   tb_line                 imported trial-balance line (immutable evidence)
--   gl_journal / gl_line    imported general-ledger entry and entry line
--   import_job              observable import progress/status
--   engagement_materiality  materiality foundation (versioned, no formula)
--   audit_area              optional account -> audit-area extension point
--
-- Conventions (unchanged from 0001):
--   * identifiers are application-generated canonical lowercase UUID text
--   * all timestamps are UTC ISO-8601 text, dates are ISO-8601 text
--   * money is signed 64-bit minor units; SQLite REAL is prohibited for money
--   * every year-owned row carries a non-null engagement_id
--   * imported financial data is append-only evidence: no ON DELETE CASCADE,
--     no UPDATE/DELETE path, and a finalized/locked period is closed to imports
--
-- The 0001-0006 schema is never edited: applied migrations are checksummed.

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------------
-- Financial period: the audit period of one engagement
-- ---------------------------------------------------------------------------
--
-- The engagement remains the isolation/lock boundary and the financial year
-- remains the authoritative date definition (period_start/period_end). The
-- financial period adds audit-facing metadata that does not belong to either:
-- the reporting date and the financial-data lock state. Data import into a
-- FINALIZED or LOCKED period is refused by guards below, so a later import can
-- never overwrite an agreed dataset.

CREATE TABLE financial_period (
    financial_period_id TEXT    NOT NULL PRIMARY KEY,
    engagement_id       TEXT    NOT NULL,
    financial_year_id   TEXT    NOT NULL,
    reporting_date      TEXT    NOT NULL,      -- ISO-8601; normally the period end
    status              TEXT    NOT NULL DEFAULT 'OPEN',
    created_at_utc      TEXT    NOT NULL,
    created_by          TEXT    NOT NULL,
    updated_at_utc      TEXT    NOT NULL,
    row_version         INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_financial_period_status
        CHECK (status IN ('OPEN', 'IN_PROGRESS', 'FINALIZED', 'LOCKED')),
    CONSTRAINT ck_financial_period_reporting_date
        CHECK (length(reporting_date) = 10 AND date(reporting_date) IS NOT NULL),
    CONSTRAINT ck_financial_period_row_version CHECK (row_version > 0),
    CONSTRAINT fk_financial_period_engagement FOREIGN KEY (engagement_id)
        REFERENCES engagement (engagement_id),
    CONSTRAINT fk_financial_period_year FOREIGN KEY (financial_year_id)
        REFERENCES financial_year (financial_year_id),
    CONSTRAINT fk_financial_period_created_by FOREIGN KEY (created_by)
        REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_financial_period_engagement ON financial_period (engagement_id);
CREATE INDEX ix_financial_period_year ON financial_period (financial_year_id);
CREATE INDEX ix_financial_period_status ON financial_period (status);

-- Engagements created before this migration receive a period. A finalized
-- engagement becomes a LOCKED period so no later import can touch its data.
INSERT INTO financial_period (
    financial_period_id, engagement_id, financial_year_id, reporting_date,
    status, created_at_utc, created_by, updated_at_utc, row_version)
SELECT
    lower(
        hex(randomblob(4)) || '-' ||
        hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' ||
        substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2) || '-' ||
        hex(randomblob(6))),
    e.engagement_id,
    e.financial_year_id,
    fy.period_end,
    CASE WHEN e.status = 'FINALIZED' THEN 'LOCKED' ELSE 'OPEN' END,
    e.created_at_utc,
    e.created_by,
    e.created_at_utc,
    1
FROM engagement e
JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id
WHERE NOT EXISTS (
    SELECT 1 FROM financial_period p WHERE p.engagement_id = e.engagement_id);

CREATE TRIGGER trg_financial_period_no_delete
BEFORE DELETE ON financial_period
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-DELETE: a financial period is never deleted');
END;

CREATE TRIGGER trg_financial_period_identity_immutable
BEFORE UPDATE ON financial_period
FOR EACH ROW
WHEN NEW.financial_period_id <> OLD.financial_period_id
    OR NEW.engagement_id <> OLD.engagement_id
    OR NEW.financial_year_id <> OLD.financial_year_id
    OR NEW.created_at_utc <> OLD.created_at_utc
    OR NEW.created_by <> OLD.created_by
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-IDENTITY: a financial period cannot be moved to another engagement or financial year');
END;

-- OPEN -> IN_PROGRESS -> FINALIZED -> LOCKED. LOCKED is terminal.
CREATE TRIGGER trg_financial_period_status_transition
BEFORE UPDATE OF status ON financial_period
FOR EACH ROW
WHEN NOT (
       (OLD.status = 'OPEN' AND NEW.status IN ('OPEN', 'IN_PROGRESS', 'FINALIZED', 'LOCKED'))
    OR (OLD.status = 'IN_PROGRESS' AND NEW.status IN ('IN_PROGRESS', 'FINALIZED', 'LOCKED'))
    OR (OLD.status = 'FINALIZED' AND NEW.status IN ('FINALIZED', 'LOCKED'))
    OR (OLD.status = 'LOCKED' AND NEW.status = 'LOCKED')
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-TRANSITION: unsupported financial period status transition; a locked period cannot be reopened');
END;

-- The reporting date is part of the reported financial statements; it may only
-- change while the period is still open for fieldwork.
CREATE TRIGGER trg_financial_period_reporting_date_guarded
BEFORE UPDATE OF reporting_date ON financial_period
FOR EACH ROW
WHEN OLD.status IN ('FINALIZED', 'LOCKED') AND NEW.reporting_date <> OLD.reporting_date
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-SEALED: the reporting date of a finalized or locked period cannot change');
END;

-- ---------------------------------------------------------------------------
-- Financial upload: the preserved source file of a TB/GL import
-- ---------------------------------------------------------------------------
--
-- Bytes live in the managed content-addressed attachment store (IFileStorage);
-- this table keeps the identity, provenance and server-side reference so a
-- client-supplied path is never trusted.

CREATE TABLE financial_upload (
    upload_id           TEXT    NOT NULL PRIMARY KEY,
    engagement_id       TEXT    NOT NULL,
    dataset_kind        TEXT    NOT NULL,
    file_name           TEXT    NOT NULL,
    content_type        TEXT    NOT NULL,
    size_bytes          INTEGER NOT NULL,
    sha256              TEXT    NOT NULL,
    storage_location    TEXT    NOT NULL,
    detected_format     TEXT    NOT NULL,
    detected_structure  TEXT    NOT NULL DEFAULT '{}',
    uploaded_at_utc     TEXT    NOT NULL,
    uploaded_by         TEXT    NOT NULL,
    CONSTRAINT ck_financial_upload_kind CHECK (dataset_kind IN ('TB', 'GL')),
    CONSTRAINT ck_financial_upload_size CHECK (size_bytes >= 0),
    CONSTRAINT ck_financial_upload_sha CHECK (length(sha256) = 64),
    CONSTRAINT fk_financial_upload_engagement FOREIGN KEY (engagement_id)
        REFERENCES engagement (engagement_id),
    CONSTRAINT fk_financial_upload_user FOREIGN KEY (uploaded_by) REFERENCES app_user (user_id)
) STRICT;

CREATE INDEX ix_financial_upload_engagement ON financial_upload (engagement_id, dataset_kind, uploaded_at_utc);
CREATE INDEX ix_financial_upload_hash ON financial_upload (engagement_id, dataset_kind, sha256);

CREATE TRIGGER trg_financial_upload_identity_immutable
BEFORE UPDATE ON financial_upload
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-UPLOAD-IMMUTABLE: an uploaded source file record cannot be changed');
END;

CREATE TRIGGER trg_financial_upload_no_delete_when_used
BEFORE DELETE ON financial_upload
FOR EACH ROW
WHEN EXISTS (SELECT 1 FROM dataset_import i WHERE i.source_upload_id = OLD.upload_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-UPLOAD-IN-USE: this source file is part of an import and must be retained');
END;

-- ---------------------------------------------------------------------------
-- Dataset import: one TB import version or one GL snapshot
-- ---------------------------------------------------------------------------
--
-- Versioning is the core control of this migration: an import is never edited
-- and never replaced in place. A new client file creates a new row with the
-- next import_no; the previous version stays addressable forever, and exactly
-- one version per (engagement, dataset_kind) may be active.

CREATE TABLE dataset_import (
    import_id                 TEXT    NOT NULL PRIMARY KEY,
    engagement_id             TEXT    NOT NULL,
    financial_period_id       TEXT    NOT NULL,
    dataset_kind              TEXT    NOT NULL,
    import_no                 INTEGER NOT NULL,
    snapshot_label            TEXT    NOT NULL,
    status                    TEXT    NOT NULL DEFAULT 'DRAFT',
    is_active                 INTEGER NOT NULL DEFAULT 0,
    superseded_by_import_id   TEXT    NULL,
    repeat_of_import_id       TEXT    NULL,
    source_upload_id          TEXT    NOT NULL,
    source_file_name          TEXT    NOT NULL,
    source_file_sha256        TEXT    NOT NULL,
    source_file_size_bytes    INTEGER NOT NULL,
    source_header_row_no      INTEGER NULL,
    source_sheet_name         TEXT    NULL,
    coverage_through_date     TEXT    NULL,
    column_mapping_json       TEXT    NOT NULL,
    fingerprint_hash          TEXT    NOT NULL,
    validation_json           TEXT    NOT NULL DEFAULT '{}',
    validation_status         TEXT    NOT NULL DEFAULT 'NOT_RUN',
    row_count                 INTEGER NOT NULL DEFAULT 0,
    valid_row_count           INTEGER NOT NULL DEFAULT 0,
    warning_count             INTEGER NOT NULL DEFAULT 0,
    error_count               INTEGER NOT NULL DEFAULT 0,
    out_of_period_count       INTEGER NOT NULL DEFAULT 0,
    total_debit_minor         INTEGER NOT NULL DEFAULT 0,
    total_credit_minor        INTEGER NOT NULL DEFAULT 0,
    difference_minor          INTEGER NOT NULL DEFAULT 0,
    is_balanced               INTEGER NOT NULL DEFAULT 0,
    unbalanced_override       INTEGER NOT NULL DEFAULT 0,
    imported_at_utc           TEXT    NOT NULL,
    imported_by               TEXT    NOT NULL,
    finalized_at_utc          TEXT    NULL,
    finalized_by              TEXT    NULL,
    row_version               INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_dataset_import_kind CHECK (dataset_kind IN ('TB', 'GL')),
    CONSTRAINT ck_dataset_import_status
        CHECK (status IN ('DRAFT', 'VALIDATED', 'IMPORTED', 'FINALIZED', 'SUPERSEDED')),
    CONSTRAINT ck_dataset_import_validation_status
        CHECK (validation_status IN ('NOT_RUN', 'VALID_WITH_WARNINGS', 'VALID', 'REJECTED')),
    CONSTRAINT ck_dataset_import_import_no CHECK (import_no > 0),
    CONSTRAINT ck_dataset_import_flags CHECK (is_active IN (0, 1) AND unbalanced_override IN (0, 1) AND is_balanced IN (0, 1)),
    CONSTRAINT ck_dataset_import_counts CHECK (
        row_count >= 0 AND valid_row_count >= 0 AND warning_count >= 0
        AND error_count >= 0 AND out_of_period_count >= 0),
    CONSTRAINT ck_dataset_import_sha CHECK (length(source_file_sha256) = 64),
    CONSTRAINT ck_dataset_import_fingerprint CHECK (length(fingerprint_hash) = 64),
    CONSTRAINT ck_dataset_import_self_reference CHECK (
        (superseded_by_import_id IS NULL OR superseded_by_import_id <> import_id)
        AND (repeat_of_import_id IS NULL OR repeat_of_import_id <> import_id)),
    -- Finalization metadata belongs to a FINALIZED version and is retained when a
    -- later version supersedes it (the earlier evidence stays finalized).
    CONSTRAINT ck_dataset_import_finalization CHECK (
        CASE
            WHEN status = 'FINALIZED'
                THEN finalized_at_utc IS NOT NULL AND finalized_by IS NOT NULL
            WHEN status = 'SUPERSEDED'
                THEN (finalized_at_utc IS NULL) = (finalized_by IS NULL)
            ELSE finalized_at_utc IS NULL AND finalized_by IS NULL
        END),
    -- A replacement link exists exactly for superseded versions.
    CONSTRAINT ck_dataset_import_supersede CHECK (
        CASE WHEN status = 'SUPERSEDED'
            THEN superseded_by_import_id IS NOT NULL
            ELSE superseded_by_import_id IS NULL
        END),
    CONSTRAINT fk_dataset_import_engagement FOREIGN KEY (engagement_id)
        REFERENCES engagement (engagement_id),
    CONSTRAINT fk_dataset_import_period FOREIGN KEY (financial_period_id)
        REFERENCES financial_period (financial_period_id),
    CONSTRAINT fk_dataset_import_upload FOREIGN KEY (source_upload_id)
        REFERENCES financial_upload (upload_id),
    CONSTRAINT fk_dataset_import_imported_by FOREIGN KEY (imported_by) REFERENCES app_user (user_id),
    CONSTRAINT fk_dataset_import_finalized_by FOREIGN KEY (finalized_by) REFERENCES app_user (user_id),
    CONSTRAINT fk_dataset_import_superseded_by FOREIGN KEY (superseded_by_import_id)
        REFERENCES dataset_import (import_id),
    CONSTRAINT fk_dataset_import_repeat_of FOREIGN KEY (repeat_of_import_id)
        REFERENCES dataset_import (import_id)
) STRICT;

CREATE UNIQUE INDEX ux_dataset_import_number ON dataset_import (engagement_id, dataset_kind, import_no);
CREATE UNIQUE INDEX ux_dataset_import_active ON dataset_import (engagement_id, dataset_kind)
    WHERE is_active = 1;
CREATE INDEX ix_dataset_import_period ON dataset_import (financial_period_id, dataset_kind, import_no);
CREATE INDEX ix_dataset_import_engagement ON dataset_import (engagement_id, dataset_kind, imported_at_utc);
CREATE INDEX ix_dataset_import_source_hash ON dataset_import (engagement_id, dataset_kind, source_file_sha256);
CREATE INDEX ix_dataset_import_fingerprint ON dataset_import (engagement_id, dataset_kind, fingerprint_hash);

CREATE TRIGGER trg_dataset_import_no_delete
BEFORE DELETE ON dataset_import
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-DELETE: imported financial data is retained as audit evidence');
END;

-- Identity and provenance are fixed when the import is created.
CREATE TRIGGER trg_dataset_import_identity_immutable
BEFORE UPDATE ON dataset_import
FOR EACH ROW
WHEN NEW.import_id <> OLD.import_id
    OR NEW.engagement_id <> OLD.engagement_id
    OR NEW.financial_period_id <> OLD.financial_period_id
    OR NEW.dataset_kind <> OLD.dataset_kind
    OR NEW.import_no <> OLD.import_no
    OR NEW.source_upload_id <> OLD.source_upload_id
    OR NEW.source_file_name <> OLD.source_file_name
    OR NEW.source_file_sha256 <> OLD.source_file_sha256
    OR NEW.source_file_size_bytes <> OLD.source_file_size_bytes
    OR NEW.column_mapping_json <> OLD.column_mapping_json
    OR NEW.fingerprint_hash <> OLD.fingerprint_hash
    OR NEW.imported_at_utc <> OLD.imported_at_utc
    OR NEW.imported_by <> OLD.imported_by
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-IDENTITY: import identity, source file and column mapping are immutable');
END;

-- Once rows are committed the measured statistics are evidence, not scratch data.
CREATE TRIGGER trg_dataset_import_stats_immutable
BEFORE UPDATE ON dataset_import
FOR EACH ROW
WHEN OLD.status IN ('IMPORTED', 'FINALIZED', 'SUPERSEDED')
 AND (NEW.validation_json <> OLD.validation_json
    OR NEW.validation_status <> OLD.validation_status
    OR NEW.row_count <> OLD.row_count
    OR NEW.valid_row_count <> OLD.valid_row_count
    OR NEW.warning_count <> OLD.warning_count
    OR NEW.error_count <> OLD.error_count
    OR NEW.out_of_period_count <> OLD.out_of_period_count
    OR NEW.total_debit_minor <> OLD.total_debit_minor
    OR NEW.total_credit_minor <> OLD.total_credit_minor
    OR NEW.difference_minor <> OLD.difference_minor
    OR NEW.is_balanced <> OLD.is_balanced
    OR NEW.unbalanced_override <> OLD.unbalanced_override)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-STATS: import statistics are frozen once rows are committed');
END;

CREATE TRIGGER trg_dataset_import_status_transition
BEFORE UPDATE OF status ON dataset_import
FOR EACH ROW
WHEN NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('DRAFT', 'VALIDATED', 'IMPORTED'))
    OR (OLD.status = 'VALIDATED' AND NEW.status IN ('VALIDATED', 'IMPORTED'))
    OR (OLD.status = 'IMPORTED' AND NEW.status IN ('IMPORTED', 'FINALIZED', 'SUPERSEDED'))
    OR (OLD.status = 'FINALIZED' AND NEW.status IN ('FINALIZED', 'SUPERSEDED'))
    OR (OLD.status = 'SUPERSEDED' AND NEW.status = 'SUPERSEDED')
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-TRANSITION: unsupported import status transition');
END;

-- A version can only be committed or activated while the period is writable.
CREATE TRIGGER trg_dataset_import_insert_period_writable
BEFORE INSERT ON dataset_import
FOR EACH ROW
WHEN (SELECT p.status FROM financial_period p
        WHERE p.financial_period_id = NEW.financial_period_id) IN ('FINALIZED', 'LOCKED')
  OR (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-LOCKED: this financial period is closed to new imports; reopen a new period or create a new version process');
END;

CREATE TRIGGER trg_dataset_import_commit_period_writable
BEFORE UPDATE OF status ON dataset_import
FOR EACH ROW
WHEN NEW.status = 'IMPORTED' AND (
       (SELECT p.status FROM financial_period p
         WHERE p.financial_period_id = NEW.financial_period_id) IN ('FINALIZED', 'LOCKED')
    OR (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-LOCKED: this financial period was closed before the import could be committed');
END;

-- Activation is only allowed for a version that is (or is becoming, in the same
-- statement) committed: a DRAFT/VALIDATED import has no evidence to activate.
CREATE TRIGGER trg_dataset_import_activation_requires_rows
BEFORE UPDATE OF is_active ON dataset_import
FOR EACH ROW
WHEN NEW.is_active = 1 AND OLD.is_active = 0
 AND OLD.status NOT IN ('IMPORTED', 'FINALIZED')
 AND NEW.status NOT IN ('IMPORTED', 'FINALIZED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-ACTIVATION: only a committed import can become the active version');
END;

-- A superseded version must name its replacement, and the replacement must be
-- a later version of the same dataset.
CREATE TRIGGER trg_dataset_import_supersede_link
BEFORE UPDATE OF superseded_by_import_id ON dataset_import
FOR EACH ROW
WHEN NEW.superseded_by_import_id IS NOT NULL
 AND NOT EXISTS (
    SELECT 1 FROM dataset_import n
    WHERE n.import_id = NEW.superseded_by_import_id
      AND n.engagement_id = NEW.engagement_id
      AND n.dataset_kind = NEW.dataset_kind
      AND n.import_no > NEW.import_no)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-SUPERSEDE: the replacement version must be a later import of the same dataset');
END;

-- The source file of an import must be an upload of the same engagement and the
-- same dataset kind: an upload id taken from another engagement, or a trial
-- balance file used as a ledger source, is refused.
CREATE TRIGGER trg_dataset_import_upload_scope
BEFORE INSERT ON dataset_import
FOR EACH ROW
WHEN NOT EXISTS (
    SELECT 1 FROM financial_upload u
    WHERE u.upload_id = NEW.source_upload_id
      AND u.engagement_id = NEW.engagement_id
      AND u.dataset_kind = NEW.dataset_kind)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-UPLOAD: the source file must be an upload of this engagement and dataset');
END;

-- ---------------------------------------------------------------------------
-- Trial balance lines: immutable imported evidence
-- ---------------------------------------------------------------------------

CREATE TABLE tb_line (
    tb_line_id         TEXT    NOT NULL PRIMARY KEY,
    import_id          TEXT    NOT NULL,
    engagement_id      TEXT    NOT NULL,
    financial_period_id TEXT   NOT NULL,
    account_id         TEXT    NOT NULL,
    line_no            INTEGER NOT NULL,
    source_row_no      INTEGER NULL,
    account_code       TEXT    NOT NULL,
    account_name       TEXT    NOT NULL,
    normalized_code    TEXT    NOT NULL,
    debit_minor        INTEGER NOT NULL DEFAULT 0,
    credit_minor       INTEGER NOT NULL DEFAULT 0,
    balance_minor      INTEGER NOT NULL DEFAULT 0,
    currency_code      TEXT    NOT NULL,
    cost_center        TEXT    NULL,
    account_group      TEXT    NULL,
    extra_columns_json TEXT    NOT NULL DEFAULT '{}',
    row_hash           TEXT    NOT NULL,
    created_at_utc     TEXT    NOT NULL,
    CONSTRAINT ck_tb_line_line_no CHECK (line_no > 0),
    CONSTRAINT ck_tb_line_amounts CHECK (debit_minor >= 0 AND credit_minor >= 0),
    CONSTRAINT ck_tb_line_code CHECK (length(trim(account_code)) > 0),
    CONSTRAINT ck_tb_line_hash CHECK (length(row_hash) = 64),
    CONSTRAINT fk_tb_line_import FOREIGN KEY (import_id) REFERENCES dataset_import (import_id),
    CONSTRAINT fk_tb_line_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_tb_line_period FOREIGN KEY (financial_period_id) REFERENCES financial_period (financial_period_id),
    -- Composite ownership FK: an imported line can only reference an account of the same engagement.
    CONSTRAINT fk_tb_line_account FOREIGN KEY (account_id, engagement_id)
        REFERENCES account (account_id, engagement_id)
) STRICT;

CREATE UNIQUE INDEX ux_tb_line_account ON tb_line (import_id, account_code);
CREATE INDEX ix_tb_line_import ON tb_line (import_id, line_no);
CREATE INDEX ix_tb_line_engagement_account ON tb_line (engagement_id, account_id);
CREATE INDEX ix_tb_line_group ON tb_line (import_id, account_group);

CREATE TRIGGER trg_tb_line_period_writable
BEFORE INSERT ON tb_line
FOR EACH ROW
WHEN (SELECT p.status FROM financial_period p
        WHERE p.financial_period_id = NEW.financial_period_id) IN ('FINALIZED', 'LOCKED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-LOCKED: trial-balance lines cannot be imported into a closed period');
END;

CREATE TRIGGER trg_tb_line_import_writable
BEFORE INSERT ON tb_line
FOR EACH ROW
WHEN (SELECT i.status FROM dataset_import i WHERE i.import_id = NEW.import_id) NOT IN ('DRAFT', 'VALIDATED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-LOCKED: lines can only be written to a draft import');
END;

CREATE TRIGGER trg_tb_line_append_only_update
BEFORE UPDATE ON tb_line
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-TB-APPEND-ONLY: imported trial-balance lines are append-only evidence; import a new version instead');
END;

CREATE TRIGGER trg_tb_line_append_only_delete
BEFORE DELETE ON tb_line
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-TB-APPEND-ONLY: imported trial-balance lines cannot be deleted');
END;

-- ---------------------------------------------------------------------------
-- General ledger: journal (entry) identity and journal-line identity
-- ---------------------------------------------------------------------------
--
-- The database id identifies the stored row. journal_identity / line_identity
-- identify the client's transaction and are derived deterministically from the
-- source when the client system does not supply a stable key, so two snapshots
-- of the same period remain comparable (future roll-forward).

CREATE TABLE gl_journal (
    gl_journal_id        TEXT    NOT NULL PRIMARY KEY,
    import_id            TEXT    NOT NULL,
    engagement_id        TEXT    NOT NULL,
    financial_period_id  TEXT    NOT NULL,
    journal_identity     TEXT    NOT NULL,
    identity_source      TEXT    NOT NULL,
    journal_number       TEXT    NULL,
    journal_source       TEXT    NULL,
    posting_date         TEXT    NULL,
    reference            TEXT    NULL,
    description          TEXT    NULL,
    currency_code        TEXT    NULL,
    prepared_by          TEXT    NULL,
    line_count           INTEGER NOT NULL DEFAULT 0,
    journal_hash         TEXT    NOT NULL,
    created_at_utc       TEXT    NOT NULL,
    CONSTRAINT ck_gl_journal_identity_source CHECK (identity_source IN ('SOURCE', 'DERIVED')),
    CONSTRAINT ck_gl_journal_line_count CHECK (line_count >= 0),
    CONSTRAINT ck_gl_journal_hash CHECK (length(journal_hash) = 64),
    CONSTRAINT ck_gl_journal_identity CHECK (length(trim(journal_identity)) > 0),
    CONSTRAINT fk_gl_journal_import FOREIGN KEY (import_id) REFERENCES dataset_import (import_id),
    CONSTRAINT fk_gl_journal_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_gl_journal_period FOREIGN KEY (financial_period_id) REFERENCES financial_period (financial_period_id)
) STRICT;

CREATE UNIQUE INDEX ux_gl_journal_identity ON gl_journal (import_id, journal_identity);
CREATE INDEX ix_gl_journal_import ON gl_journal (import_id);
CREATE INDEX ix_gl_journal_number ON gl_journal (import_id, journal_number);

CREATE TABLE gl_line (
    gl_line_id           TEXT    NOT NULL PRIMARY KEY,
    gl_journal_id        TEXT    NOT NULL,
    import_id            TEXT    NOT NULL,
    engagement_id        TEXT    NOT NULL,
    financial_period_id  TEXT    NOT NULL,
    line_no              INTEGER NOT NULL,
    source_row_no        INTEGER NULL,
    line_identity        TEXT    NOT NULL,
    identity_source      TEXT    NOT NULL,
    source_line_no       TEXT    NULL,
    account_id           TEXT    NULL,
    account_code         TEXT    NOT NULL,
    account_name         TEXT    NULL,
    transaction_date     TEXT    NOT NULL,
    posting_date         TEXT    NULL,
    description          TEXT    NOT NULL DEFAULT '',
    debit_minor          INTEGER NOT NULL DEFAULT 0,
    credit_minor         INTEGER NOT NULL DEFAULT 0,
    amount_minor         INTEGER NOT NULL DEFAULT 0,
    currency_code        TEXT    NULL,
    journal_source       TEXT    NULL,
    reference            TEXT    NULL,
    prepared_by          TEXT    NULL,
    is_out_of_period     INTEGER NOT NULL DEFAULT 0,
    line_hash            TEXT    NOT NULL,
    value_hash           TEXT    NOT NULL,
    attribute_hash       TEXT    NOT NULL,
    extra_columns_json   TEXT    NOT NULL DEFAULT '{}',
    created_at_utc       TEXT    NOT NULL,
    CONSTRAINT ck_gl_line_line_no CHECK (line_no > 0),
    CONSTRAINT ck_gl_line_identity_source CHECK (identity_source IN ('SOURCE', 'DERIVED')),
    CONSTRAINT ck_gl_line_amounts CHECK (debit_minor >= 0 AND credit_minor >= 0),
    CONSTRAINT ck_gl_line_code CHECK (length(trim(account_code)) > 0),
    CONSTRAINT ck_gl_line_date CHECK (length(transaction_date) = 10 AND date(transaction_date) IS NOT NULL),
    CONSTRAINT ck_gl_line_out_of_period CHECK (is_out_of_period IN (0, 1)),
    CONSTRAINT ck_gl_line_hashes CHECK (length(line_hash) = 64 AND length(value_hash) = 64 AND length(attribute_hash) = 64),
    CONSTRAINT fk_gl_line_journal FOREIGN KEY (gl_journal_id) REFERENCES gl_journal (gl_journal_id),
    CONSTRAINT fk_gl_line_import FOREIGN KEY (import_id) REFERENCES dataset_import (import_id),
    CONSTRAINT fk_gl_line_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_gl_line_period FOREIGN KEY (financial_period_id) REFERENCES financial_period (financial_period_id),
    -- Nullable composite ownership FK: a line is linked to the account master only
    -- when the account exists in this engagement (GL-only accounts stay unlinked
    -- and are reported as "missing from TB" instead of being invented).
    CONSTRAINT fk_gl_line_account FOREIGN KEY (account_id, engagement_id)
        REFERENCES account (account_id, engagement_id)
) STRICT;

CREATE UNIQUE INDEX ux_gl_line_identity ON gl_line (import_id, gl_journal_id, line_identity);
CREATE INDEX ix_gl_line_import_account ON gl_line (import_id, account_code);
CREATE INDEX ix_gl_line_import_date ON gl_line (import_id, transaction_date);
CREATE INDEX ix_gl_line_journal ON gl_line (gl_journal_id, line_no);
CREATE INDEX ix_gl_line_out_of_period ON gl_line (import_id, is_out_of_period);
CREATE INDEX ix_gl_line_engagement_account ON gl_line (engagement_id, account_id);

CREATE TRIGGER trg_gl_journal_period_writable
BEFORE INSERT ON gl_journal
FOR EACH ROW
WHEN (SELECT p.status FROM financial_period p
        WHERE p.financial_period_id = NEW.financial_period_id) IN ('FINALIZED', 'LOCKED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-LOCKED: general-ledger entries cannot be imported into a closed period');
END;

CREATE TRIGGER trg_gl_journal_import_writable
BEFORE INSERT ON gl_journal
FOR EACH ROW
WHEN (SELECT i.status FROM dataset_import i WHERE i.import_id = NEW.import_id) NOT IN ('DRAFT', 'VALIDATED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-LOCKED: entries can only be written to a draft import');
END;

CREATE TRIGGER trg_gl_line_period_writable
BEFORE INSERT ON gl_line
FOR EACH ROW
WHEN (SELECT p.status FROM financial_period p
        WHERE p.financial_period_id = NEW.financial_period_id) IN ('FINALIZED', 'LOCKED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PERIOD-LOCKED: general-ledger lines cannot be imported into a closed period');
END;

CREATE TRIGGER trg_gl_line_import_writable
BEFORE INSERT ON gl_line
FOR EACH ROW
WHEN (SELECT i.status FROM dataset_import i WHERE i.import_id = NEW.import_id) NOT IN ('DRAFT', 'VALIDATED')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-DATASET-LOCKED: lines can only be written to a draft import');
END;

CREATE TRIGGER trg_gl_journal_append_only_update
BEFORE UPDATE ON gl_journal
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-GL-APPEND-ONLY: imported journals are append-only evidence; import a new version instead');
END;

CREATE TRIGGER trg_gl_journal_append_only_delete
BEFORE DELETE ON gl_journal
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-GL-APPEND-ONLY: imported journals cannot be deleted');
END;

CREATE TRIGGER trg_gl_line_append_only_update
BEFORE UPDATE ON gl_line
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-GL-APPEND-ONLY: imported general-ledger lines are append-only evidence; import a new version instead');
END;

CREATE TRIGGER trg_gl_line_append_only_delete
BEFORE DELETE ON gl_line
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-GL-APPEND-ONLY: imported general-ledger lines cannot be deleted');
END;

-- ---------------------------------------------------------------------------
-- Import job: observable, resumable status of a TB/GL import
-- ---------------------------------------------------------------------------
--
-- The local architecture has no distributed job system. A job row makes the
-- import observable (queued -> validating -> importing -> reconciling ->
-- completed/failed/cancelled) and keeps the result addressable after the
-- request that started it has finished.

CREATE TABLE import_job (
    job_id            TEXT    NOT NULL PRIMARY KEY,
    engagement_id     TEXT    NOT NULL,
    financial_period_id TEXT  NOT NULL,
    dataset_kind      TEXT    NOT NULL,
    status            TEXT    NOT NULL DEFAULT 'QUEUED',
    stage             TEXT    NOT NULL DEFAULT 'QUEUED',
    upload_id         TEXT    NULL,
    import_id         TEXT    NULL,
    column_mapping_json TEXT  NOT NULL DEFAULT '{}',
    attempt_no        INTEGER NOT NULL DEFAULT 1,
    allow_unbalanced  INTEGER NOT NULL DEFAULT 0,
    allow_repeat      INTEGER NOT NULL DEFAULT 0,
    cancel_requested  INTEGER NOT NULL DEFAULT 0,
    processed_rows    INTEGER NOT NULL DEFAULT 0,
    total_rows        INTEGER NOT NULL DEFAULT 0,
    message           TEXT    NOT NULL DEFAULT '',
    error_code        TEXT    NULL,
    requested_at_utc  TEXT    NOT NULL,
    requested_by      TEXT    NOT NULL,
    started_at_utc    TEXT    NULL,
    completed_at_utc  TEXT    NULL,
    CONSTRAINT ck_import_job_kind CHECK (dataset_kind IN ('TB', 'GL')),
    CONSTRAINT ck_import_job_status CHECK (status IN
        ('QUEUED', 'PROCESSING', 'VALIDATING', 'IMPORTING', 'RECONCILING', 'COMPLETED', 'FAILED', 'CANCELLED')),
    CONSTRAINT ck_import_job_stage CHECK (stage IN
        ('QUEUED', 'PROCESSING', 'VALIDATING', 'IMPORTING', 'RECONCILING', 'COMPLETED', 'FAILED', 'CANCELLED')),
    CONSTRAINT ck_import_job_flags CHECK (
        allow_unbalanced IN (0, 1) AND allow_repeat IN (0, 1) AND cancel_requested IN (0, 1)),
    CONSTRAINT ck_import_job_counts CHECK (processed_rows >= 0 AND total_rows >= 0 AND attempt_no > 0),
    CONSTRAINT fk_import_job_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_import_job_period FOREIGN KEY (financial_period_id) REFERENCES financial_period (financial_period_id),
    CONSTRAINT fk_import_job_upload FOREIGN KEY (upload_id) REFERENCES financial_upload (upload_id),
    CONSTRAINT fk_import_job_import FOREIGN KEY (import_id) REFERENCES dataset_import (import_id),
    CONSTRAINT fk_import_job_user FOREIGN KEY (requested_by) REFERENCES app_user (user_id)
) STRICT;

CREATE INDEX ix_import_job_engagement ON import_job (engagement_id, requested_at_utc);
CREATE INDEX ix_import_job_status ON import_job (status, requested_at_utc);

CREATE TRIGGER trg_import_job_no_delete
BEFORE DELETE ON import_job
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-JOB-DELETE: import job history is retained');
END;

CREATE TRIGGER trg_import_job_identity_immutable
BEFORE UPDATE ON import_job
FOR EACH ROW
WHEN NEW.job_id <> OLD.job_id
    OR NEW.engagement_id <> OLD.engagement_id
    OR NEW.financial_period_id <> OLD.financial_period_id
    OR NEW.dataset_kind <> OLD.dataset_kind
    OR NEW.upload_id IS NOT OLD.upload_id
    OR NEW.requested_at_utc <> OLD.requested_at_utc
    OR NEW.requested_by <> OLD.requested_by
    OR NEW.attempt_no <> OLD.attempt_no
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-JOB-IDENTITY: import job identity and origin are immutable');
END;

CREATE TRIGGER trg_import_job_transition
BEFORE UPDATE OF status ON import_job
FOR EACH ROW
WHEN NOT (
       (OLD.status = 'QUEUED' AND NEW.status IN ('QUEUED', 'PROCESSING', 'CANCELLED', 'FAILED'))
    OR (OLD.status = 'PROCESSING' AND NEW.status IN ('PROCESSING', 'VALIDATING', 'FAILED', 'CANCELLED'))
    OR (OLD.status = 'VALIDATING' AND NEW.status IN ('VALIDATING', 'IMPORTING', 'FAILED', 'CANCELLED'))
    OR (OLD.status = 'IMPORTING' AND NEW.status IN ('IMPORTING', 'RECONCILING', 'FAILED', 'CANCELLED'))
    OR (OLD.status = 'RECONCILING' AND NEW.status IN ('RECONCILING', 'COMPLETED', 'FAILED', 'CANCELLED'))
    OR (OLD.status IN ('COMPLETED', 'FAILED', 'CANCELLED') AND NEW.status = OLD.status)
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-JOB-TRANSITION: unsupported import job status transition');
END;

-- ---------------------------------------------------------------------------
-- Materiality foundation: versioned, no formula baked into the schema
-- ---------------------------------------------------------------------------

CREATE TABLE engagement_materiality (
    materiality_id                    TEXT    NOT NULL PRIMARY KEY,
    engagement_id                     TEXT    NOT NULL,
    financial_period_id               TEXT    NOT NULL,
    version_no                        INTEGER NOT NULL,
    status                            TEXT    NOT NULL DEFAULT 'DRAFT',
    overall_materiality_minor         INTEGER NOT NULL,
    performance_materiality_minor     INTEGER NULL,
    clearly_trivial_threshold_minor   INTEGER NULL,
    currency_code                     TEXT    NOT NULL,
    basis_note                        TEXT    NOT NULL DEFAULT '',
    determined_at_utc                 TEXT    NOT NULL,
    determined_by                     TEXT    NOT NULL,
    approved_at_utc                   TEXT    NULL,
    approved_by                       TEXT    NULL,
    supersedes_id                     TEXT    NULL,
    row_version                       INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_materiality_status CHECK (status IN ('DRAFT', 'APPROVED', 'SUPERSEDED')),
    CONSTRAINT ck_materiality_version CHECK (version_no > 0),
    CONSTRAINT ck_materiality_positive CHECK (overall_materiality_minor > 0),
    CONSTRAINT ck_materiality_thresholds CHECK (
        (performance_materiality_minor IS NULL OR performance_materiality_minor > 0)
        AND (clearly_trivial_threshold_minor IS NULL OR clearly_trivial_threshold_minor > 0)),
    CONSTRAINT ck_materiality_approval CHECK (
        (status = 'APPROVED' AND approved_at_utc IS NOT NULL AND approved_by IS NOT NULL)
        OR (status <> 'APPROVED')),
    CONSTRAINT ck_materiality_self_reference CHECK (supersedes_id IS NULL OR supersedes_id <> materiality_id),
    CONSTRAINT ck_materiality_first_version CHECK (
        (version_no = 1 AND supersedes_id IS NULL) OR (version_no > 1 AND supersedes_id IS NOT NULL)),
    CONSTRAINT fk_materiality_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_materiality_period FOREIGN KEY (financial_period_id) REFERENCES financial_period (financial_period_id),
    CONSTRAINT fk_materiality_determined_by FOREIGN KEY (determined_by) REFERENCES app_user (user_id),
    CONSTRAINT fk_materiality_approved_by FOREIGN KEY (approved_by) REFERENCES app_user (user_id),
    CONSTRAINT fk_materiality_supersedes FOREIGN KEY (supersedes_id) REFERENCES engagement_materiality (materiality_id)
) STRICT;

CREATE UNIQUE INDEX ux_materiality_version ON engagement_materiality (engagement_id, version_no);
CREATE INDEX ix_materiality_engagement ON engagement_materiality (engagement_id, determined_at_utc);

CREATE TRIGGER trg_materiality_insert_locked
BEFORE INSERT ON engagement_materiality
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MATERIALITY-FINALIZED: materiality cannot be recorded in a finalized engagement');
END;

CREATE TRIGGER trg_materiality_identity_immutable
BEFORE UPDATE ON engagement_materiality
FOR EACH ROW
WHEN NEW.materiality_id <> OLD.materiality_id
    OR NEW.engagement_id <> OLD.engagement_id
    OR NEW.financial_period_id <> OLD.financial_period_id
    OR NEW.version_no <> OLD.version_no
    OR NEW.overall_materiality_minor <> OLD.overall_materiality_minor
    OR NEW.performance_materiality_minor IS NOT OLD.performance_materiality_minor
    OR NEW.clearly_trivial_threshold_minor IS NOT OLD.clearly_trivial_threshold_minor
    OR NEW.currency_code <> OLD.currency_code
    OR NEW.basis_note <> OLD.basis_note
    OR NEW.determined_at_utc <> OLD.determined_at_utc
    OR NEW.determined_by <> OLD.determined_by
    OR NEW.supersedes_id IS NOT OLD.supersedes_id
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MATERIALITY-IMMUTABLE: record a new materiality version instead of editing this one');
END;

CREATE TRIGGER trg_materiality_status_transition
BEFORE UPDATE OF status ON engagement_materiality
FOR EACH ROW
WHEN NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('DRAFT', 'APPROVED', 'SUPERSEDED'))
    OR (OLD.status = 'APPROVED' AND NEW.status IN ('APPROVED', 'SUPERSEDED'))
    OR (OLD.status = 'SUPERSEDED' AND NEW.status = 'SUPERSEDED')
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MATERIALITY-TRANSITION: unsupported materiality status transition');
END;

CREATE TRIGGER trg_materiality_no_delete
BEFORE DELETE ON engagement_materiality
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MATERIALITY-DELETE: materiality versions are retained');
END;

-- ---------------------------------------------------------------------------
-- Audit areas: optional extension point for later account classification
-- ---------------------------------------------------------------------------

CREATE TABLE audit_area (
    audit_area_id  TEXT    NOT NULL PRIMARY KEY,
    engagement_id  TEXT    NOT NULL,
    area_code      TEXT    NOT NULL,
    area_name      TEXT    NOT NULL,
    display_order  INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT    NOT NULL,
    created_by     TEXT    NOT NULL,
    row_version    INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_audit_area_code CHECK (area_code = upper(trim(area_code)) AND length(area_code) > 0),
    CONSTRAINT ck_audit_area_name CHECK (length(trim(area_name)) > 0),
    CONSTRAINT ck_audit_area_order CHECK (display_order >= 0),
    CONSTRAINT ck_audit_area_row_version CHECK (row_version > 0),
    CONSTRAINT fk_audit_area_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_audit_area_created_by FOREIGN KEY (created_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_audit_area_code ON audit_area (engagement_id, area_code);
CREATE UNIQUE INDEX ux_audit_area_id_engagement ON audit_area (audit_area_id, engagement_id);

CREATE TRIGGER trg_audit_area_insert_locked
BEFORE INSERT ON audit_area
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-AUDIT-AREA-FINALIZED: audit areas cannot be added to a finalized engagement');
END;

CREATE TRIGGER trg_audit_area_identity_immutable
BEFORE UPDATE ON audit_area
FOR EACH ROW
WHEN NEW.audit_area_id <> OLD.audit_area_id
    OR NEW.engagement_id <> OLD.engagement_id
    OR NEW.created_at_utc <> OLD.created_at_utc
    OR NEW.created_by <> OLD.created_by
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-AUDIT-AREA-IDENTITY: an audit area cannot be moved to another engagement');
END;

CREATE TRIGGER trg_audit_area_no_delete
BEFORE DELETE ON audit_area
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-AUDIT-AREA-DELETE: audit areas are retained; rename or reassign accounts instead');
END;

-- ---------------------------------------------------------------------------
-- Account master: normalization, grouping and audit-area link
-- ---------------------------------------------------------------------------

ALTER TABLE account ADD COLUMN normalized_code TEXT NULL;
ALTER TABLE account ADD COLUMN account_group TEXT NULL;
ALTER TABLE account ADD COLUMN account_origin TEXT NOT NULL DEFAULT 'MANUAL';
ALTER TABLE account ADD COLUMN audit_area_id TEXT NULL;

-- Backfill: normalization is deterministic (upper case, separators removed).
UPDATE account
   SET normalized_code = replace(replace(replace(replace(replace(
           replace(upper(trim(account_code)), ' ', ''), '-', ''), '.', ''), '/', ''), '_', ''), ',', '')
 WHERE normalized_code IS NULL;

CREATE INDEX ix_account_normalized ON account (engagement_id, normalized_code);
CREATE INDEX ix_account_area ON account (engagement_id, audit_area_id);

CREATE TRIGGER trg_account_origin_valid
BEFORE INSERT ON account
FOR EACH ROW
WHEN NEW.account_origin NOT IN ('MANUAL', 'TB', 'GL')
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-ORIGIN: unsupported account origin');
END;

CREATE TRIGGER trg_account_origin_immutable
BEFORE UPDATE OF account_origin ON account
FOR EACH ROW
WHEN NEW.account_origin <> OLD.account_origin
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-ORIGIN: account origin is provenance and cannot be rewritten');
END;

-- An account may only be linked to an audit area of the same engagement.
CREATE TRIGGER trg_account_audit_area_same_engagement_insert
BEFORE INSERT ON account
FOR EACH ROW
WHEN NEW.audit_area_id IS NOT NULL
 AND NOT EXISTS (
    SELECT 1 FROM audit_area a
    WHERE a.audit_area_id = NEW.audit_area_id AND a.engagement_id = NEW.engagement_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-AREA: an account can only be linked to an audit area of the same engagement');
END;

CREATE TRIGGER trg_account_audit_area_same_engagement_update
BEFORE UPDATE OF audit_area_id ON account
FOR EACH ROW
WHEN NEW.audit_area_id IS NOT NULL
 AND NOT EXISTS (
    SELECT 1 FROM audit_area a
    WHERE a.audit_area_id = NEW.audit_area_id AND a.engagement_id = NEW.engagement_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-AREA: an account can only be linked to an audit area of the same engagement');
END;
