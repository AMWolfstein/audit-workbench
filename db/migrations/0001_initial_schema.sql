-- Audit Workbench migration 0001 - initial schema
--
-- Canonical schema definition. This file is the single source of truth for the
-- SQLite workspace structure. It is applied by:
--   * the .NET application (SqlMigrationRunner, embedded resource), and
--   * the cross-runtime verification harness (tools/verification).
--
-- Conventions
--   * identifiers are application-generated canonical lowercase UUID text (36 chars)
--   * all timestamps are UTC ISO-8601 text: YYYY-MM-DDTHH:MM:SS.fffZ
--   * dates are ISO-8601 text: YYYY-MM-DD
--   * money is stored as signed 64-bit minor units (ADR-009); SQLite REAL is prohibited
--   * every year-owned table carries a non-null engagement_id (invariant 1)
--   * no business table has an ON DELETE CASCADE; deletes are RESTRICT by default

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------------
-- Schema/workspace metadata
-- ---------------------------------------------------------------------------

CREATE TABLE schema_migration (
    migration_id    TEXT    NOT NULL PRIMARY KEY,
    checksum_sha256 TEXT    NOT NULL,
    applied_at_utc  TEXT    NOT NULL
) STRICT;

CREATE TABLE workspace_metadata (
    metadata_key   TEXT NOT NULL PRIMARY KEY,
    metadata_value TEXT NOT NULL
) STRICT;

-- ---------------------------------------------------------------------------
-- Identity reference data (MVP: a single local, clearly non-production actor)
-- ---------------------------------------------------------------------------

CREATE TABLE app_user (
    user_id        TEXT NOT NULL PRIMARY KEY,
    username       TEXT NOT NULL,
    display_name   TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'ACTIVE',
    is_local_demo  INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    CONSTRAINT ck_app_user_status CHECK (status IN ('ACTIVE', 'DISABLED')),
    CONSTRAINT ck_app_user_demo_flag CHECK (is_local_demo IN (0, 1)),
    CONSTRAINT ck_app_user_username CHECK (username = lower(trim(username)) AND length(username) > 0)
) STRICT;

CREATE UNIQUE INDEX ux_app_user_username ON app_user (username);

-- ---------------------------------------------------------------------------
-- Company master data
-- ---------------------------------------------------------------------------

CREATE TABLE company (
    company_id     TEXT NOT NULL PRIMARY KEY,
    legal_name     TEXT NOT NULL,
    short_name     TEXT NOT NULL,      -- internal, non-sensitive office reference
    industry       TEXT NOT NULL,
    country_code   TEXT NOT NULL,      -- ISO 3166-1 alpha-2
    tax_reference  TEXT NULL,          -- optional tax / registration number
    status         TEXT NOT NULL DEFAULT 'ACTIVE',
    created_at_utc TEXT NOT NULL,
    created_by     TEXT NOT NULL,
    archived_at_utc TEXT NULL,
    row_version    INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_company_status CHECK (status IN ('ACTIVE', 'ARCHIVED')),
    CONSTRAINT ck_company_legal_name CHECK (length(trim(legal_name)) > 0),
    CONSTRAINT ck_company_short_name CHECK (length(trim(short_name)) > 0),
    CONSTRAINT ck_company_country CHECK (length(country_code) = 2 AND country_code = upper(country_code)),
    CONSTRAINT ck_company_row_version CHECK (row_version > 0),
    CONSTRAINT ck_company_archive_state CHECK (
        (status = 'ACTIVE' AND archived_at_utc IS NULL)
        OR (status = 'ARCHIVED' AND archived_at_utc IS NOT NULL)
    ),
    CONSTRAINT fk_company_created_by FOREIGN KEY (created_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_company_short_name ON company (lower(short_name));
CREATE INDEX ix_company_status ON company (status);

-- ---------------------------------------------------------------------------
-- Financial year reference data
-- ---------------------------------------------------------------------------

CREATE TABLE financial_year (
    financial_year_id TEXT NOT NULL PRIMARY KEY,
    label             TEXT NOT NULL,
    period_start      TEXT NOT NULL,
    period_end        TEXT NOT NULL,
    created_at_utc    TEXT NOT NULL,
    CONSTRAINT ck_financial_year_label CHECK (length(trim(label)) > 0),
    CONSTRAINT ck_financial_year_dates CHECK (
        period_start <= period_end
        AND length(period_start) = 10
        AND length(period_end) = 10
        AND date(period_start) IS NOT NULL
        AND date(period_end) IS NOT NULL
    )
) STRICT;

CREATE UNIQUE INDEX ux_financial_year_definition
    ON financial_year (label, period_start, period_end);
CREATE INDEX ix_financial_year_period_end ON financial_year (period_end);

-- ---------------------------------------------------------------------------
-- Engagement: aggregate root, year boundary and lock boundary
-- ---------------------------------------------------------------------------

CREATE TABLE engagement (
    engagement_id                 TEXT    NOT NULL PRIMARY KEY,
    company_id                    TEXT    NOT NULL,
    financial_year_id             TEXT    NOT NULL,
    status                        TEXT    NOT NULL DEFAULT 'DRAFT',
    currency_code                 TEXT    NOT NULL DEFAULT 'USD',
    minor_unit_scale              INTEGER NOT NULL DEFAULT 2,
    created_at_utc                TEXT    NOT NULL,
    created_by                    TEXT    NOT NULL,
    finalized_at_utc              TEXT    NULL,
    finalized_by                  TEXT    NULL,
    finalization_digest           TEXT    NULL,
    finalization_manifest_version TEXT    NULL,
    row_version                   INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT ck_engagement_status CHECK (status IN ('DRAFT', 'IN_PROGRESS', 'FINALIZED')),
    CONSTRAINT ck_engagement_currency CHECK (length(currency_code) = 3 AND currency_code = upper(currency_code)),
    CONSTRAINT ck_engagement_scale CHECK (minor_unit_scale BETWEEN 0 AND 6),
    CONSTRAINT ck_engagement_row_version CHECK (row_version > 0),
    CONSTRAINT ck_engagement_finalization_consistency CHECK (
        (status <> 'FINALIZED'
            AND finalized_at_utc IS NULL
            AND finalized_by IS NULL
            AND finalization_digest IS NULL
            AND finalization_manifest_version IS NULL)
        OR (status = 'FINALIZED'
            AND finalized_at_utc IS NOT NULL
            AND finalized_by IS NOT NULL
            AND finalization_digest IS NOT NULL
            AND finalization_manifest_version IS NOT NULL)
    ),
    CONSTRAINT fk_engagement_company FOREIGN KEY (company_id) REFERENCES company (company_id),
    CONSTRAINT fk_engagement_financial_year FOREIGN KEY (financial_year_id) REFERENCES financial_year (financial_year_id),
    CONSTRAINT fk_engagement_created_by FOREIGN KEY (created_by) REFERENCES app_user (user_id),
    CONSTRAINT fk_engagement_finalized_by FOREIGN KEY (finalized_by) REFERENCES app_user (user_id)
) STRICT;

-- Invariant 3: one engagement per company and financial year.
CREATE UNIQUE INDEX ux_engagement_company_year ON engagement (company_id, financial_year_id);
CREATE INDEX ix_engagement_company_status ON engagement (company_id, status);

-- ---------------------------------------------------------------------------
-- Account: year-owned classification
-- ---------------------------------------------------------------------------

CREATE TABLE account (
    account_id     TEXT    NOT NULL PRIMARY KEY,
    engagement_id  TEXT    NOT NULL,
    account_code   TEXT    NOT NULL,
    account_name   TEXT    NOT NULL,
    account_type   TEXT    NOT NULL DEFAULT 'UNCLASSIFIED',
    display_order  INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT    NOT NULL,
    created_by     TEXT    NOT NULL,
    CONSTRAINT ck_account_code CHECK (account_code = upper(trim(account_code)) AND length(account_code) > 0),
    CONSTRAINT ck_account_name CHECK (length(trim(account_name)) > 0),
    CONSTRAINT ck_account_type CHECK (account_type IN
        ('ASSET', 'LIABILITY', 'EQUITY', 'INCOME', 'EXPENSE', 'UNCLASSIFIED')),
    CONSTRAINT ck_account_display_order CHECK (display_order >= 0),
    CONSTRAINT fk_account_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_account_created_by FOREIGN KEY (created_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_account_engagement_code ON account (engagement_id, account_code);
-- Candidate key referenced by the composite ownership foreign key on financial_data.
CREATE UNIQUE INDEX ux_account_id_engagement ON account (account_id, engagement_id);

-- ---------------------------------------------------------------------------
-- Financial data: append-only revisions owned by one engagement
-- ---------------------------------------------------------------------------

CREATE TABLE financial_data (
    financial_data_id TEXT    NOT NULL PRIMARY KEY,
    engagement_id     TEXT    NOT NULL,
    account_id        TEXT    NOT NULL,
    revision_no       INTEGER NOT NULL,
    amount_minor      INTEGER NOT NULL,
    currency_code     TEXT    NOT NULL,
    supersedes_id     TEXT    NULL,
    correction_reason TEXT    NULL,
    recorded_at_utc   TEXT    NOT NULL,
    recorded_by       TEXT    NOT NULL,
    CONSTRAINT ck_financial_data_revision CHECK (revision_no > 0),
    CONSTRAINT ck_financial_data_currency CHECK (length(currency_code) = 3 AND currency_code = upper(currency_code)),
    CONSTRAINT ck_financial_data_self_reference CHECK (supersedes_id IS NULL OR supersedes_id <> financial_data_id),
    CONSTRAINT ck_financial_data_first_revision CHECK (
        (revision_no = 1 AND supersedes_id IS NULL)
        OR (revision_no > 1 AND supersedes_id IS NOT NULL)
    ),
    CONSTRAINT fk_financial_data_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    -- Composite ownership FK: a value can only reference an account of the same engagement.
    CONSTRAINT fk_financial_data_account FOREIGN KEY (account_id, engagement_id)
        REFERENCES account (account_id, engagement_id),
    CONSTRAINT fk_financial_data_supersedes FOREIGN KEY (supersedes_id) REFERENCES financial_data (financial_data_id),
    CONSTRAINT fk_financial_data_recorded_by FOREIGN KEY (recorded_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_financial_data_revision
    ON financial_data (engagement_id, account_id, revision_no);
CREATE INDEX ix_financial_data_account ON financial_data (account_id);

-- ---------------------------------------------------------------------------
-- Prior-year relationship: one-way, immutable link to a finalized earlier year
-- ---------------------------------------------------------------------------

CREATE TABLE prior_year_relationship (
    relationship_id       TEXT NOT NULL PRIMARY KEY,
    current_engagement_id TEXT NOT NULL,
    prior_engagement_id   TEXT NOT NULL,
    linked_at_utc         TEXT NOT NULL,
    linked_by             TEXT NOT NULL,
    CONSTRAINT ck_prior_year_distinct CHECK (current_engagement_id <> prior_engagement_id),
    CONSTRAINT fk_prior_year_current FOREIGN KEY (current_engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_prior_year_prior FOREIGN KEY (prior_engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_prior_year_linked_by FOREIGN KEY (linked_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_prior_year_current ON prior_year_relationship (current_engagement_id);
CREATE INDEX ix_prior_year_prior ON prior_year_relationship (prior_engagement_id);

-- ---------------------------------------------------------------------------
-- Finalization manifest: canonical content fixed at finalization time
-- ---------------------------------------------------------------------------

CREATE TABLE finalization_manifest (
    manifest_id       TEXT    NOT NULL PRIMARY KEY,
    engagement_id     TEXT    NOT NULL,
    manifest_version  TEXT    NOT NULL,
    root_digest       TEXT    NOT NULL,
    canonical_content TEXT    NOT NULL,
    record_count      INTEGER NOT NULL,
    created_at_utc    TEXT    NOT NULL,
    created_by        TEXT    NOT NULL,
    CONSTRAINT ck_manifest_record_count CHECK (record_count >= 0),
    CONSTRAINT ck_manifest_digest CHECK (length(root_digest) = 64),
    CONSTRAINT fk_manifest_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id),
    CONSTRAINT fk_manifest_created_by FOREIGN KEY (created_by) REFERENCES app_user (user_id)
) STRICT;

CREATE UNIQUE INDEX ux_manifest_engagement ON finalization_manifest (engagement_id);

-- ---------------------------------------------------------------------------
-- Audit trail: append-only, hash chained
-- ---------------------------------------------------------------------------

CREATE TABLE audit_event (
    audit_event_id      TEXT    NOT NULL PRIMARY KEY,
    sequence_no         INTEGER NOT NULL,
    occurred_at_utc     TEXT    NOT NULL,
    actor_user_id       TEXT    NOT NULL,
    actor_display_name  TEXT    NOT NULL,
    event_type          TEXT    NOT NULL,
    outcome             TEXT    NOT NULL DEFAULT 'SUCCESS',
    company_id          TEXT    NULL,
    engagement_id       TEXT    NULL,
    entity_type         TEXT    NOT NULL,
    entity_id           TEXT    NOT NULL,
    description         TEXT    NOT NULL,
    details_json        TEXT    NOT NULL DEFAULT '{}',
    previous_event_hash TEXT    NULL,
    event_hash          TEXT    NOT NULL,
    CONSTRAINT ck_audit_event_outcome CHECK (outcome IN ('SUCCESS', 'REJECTED')),
    CONSTRAINT ck_audit_event_sequence CHECK (sequence_no > 0),
    CONSTRAINT ck_audit_event_hash CHECK (length(event_hash) = 64),
    CONSTRAINT ck_audit_event_description CHECK (length(trim(description)) > 0),
    CONSTRAINT fk_audit_event_actor FOREIGN KEY (actor_user_id) REFERENCES app_user (user_id),
    CONSTRAINT fk_audit_event_company FOREIGN KEY (company_id) REFERENCES company (company_id),
    CONSTRAINT fk_audit_event_engagement FOREIGN KEY (engagement_id) REFERENCES engagement (engagement_id)
) STRICT;

CREATE UNIQUE INDEX ux_audit_event_sequence ON audit_event (sequence_no);
CREATE INDEX ix_audit_event_time ON audit_event (occurred_at_utc);
CREATE INDEX ix_audit_event_engagement ON audit_event (engagement_id, occurred_at_utc);
CREATE INDEX ix_audit_event_entity ON audit_event (entity_type, entity_id);
