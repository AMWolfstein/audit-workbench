-- Audit Workbench migration 0005 - AWB-CLIENT/1.0 handover support
-- Recommended decisions D-1..D-3 from docs/client-handover-package.md.

-- Financial-year ids are part of finalized manifests and must survive a handover.
-- Definitions may therefore legitimately be duplicated under different ids.
DROP INDEX ux_financial_year_definition;

ALTER TABLE app_user ADD COLUMN is_external_principal INTEGER NOT NULL DEFAULT 0
    CONSTRAINT ck_app_user_external CHECK (is_external_principal IN (0, 1));

CREATE TABLE client_import (
    import_id            TEXT NOT NULL PRIMARY KEY,
    package_id           TEXT NOT NULL,
    package_digest       TEXT NOT NULL,
    source_company_id    TEXT NOT NULL,
    imported_at_utc      TEXT NOT NULL,
    imported_by          TEXT NOT NULL,
    source_manifest_json TEXT NOT NULL,
    team_history_json    TEXT NOT NULL,
    CONSTRAINT ux_client_import_package UNIQUE (package_id),
    CONSTRAINT ck_client_import_digest CHECK (length(package_digest) = 64),
    CONSTRAINT fk_client_import_user FOREIGN KEY (imported_by) REFERENCES app_user(user_id)
) STRICT;

CREATE TABLE imported_audit_event (
    imported_audit_event_id TEXT NOT NULL PRIMARY KEY,
    import_id               TEXT NOT NULL,
    source_audit_event_id   TEXT NOT NULL,
    source_sequence_no      INTEGER NOT NULL,
    occurred_at_utc         TEXT NOT NULL,
    actor_user_id           TEXT NOT NULL,
    actor_display_name      TEXT NOT NULL,
    event_type              TEXT NOT NULL,
    outcome                 TEXT NOT NULL,
    company_id              TEXT NULL,
    engagement_id           TEXT NULL,
    entity_type             TEXT NOT NULL,
    entity_id               TEXT NOT NULL,
    description             TEXT NOT NULL,
    details_json            TEXT NOT NULL,
    previous_event_hash     TEXT NULL,
    event_hash              TEXT NOT NULL,
    CONSTRAINT fk_imported_event_import FOREIGN KEY (import_id) REFERENCES client_import(import_id),
    CONSTRAINT ux_imported_event_source UNIQUE (import_id, source_sequence_no),
    CONSTRAINT ck_imported_event_sequence CHECK (source_sequence_no > 0),
    CONSTRAINT ck_imported_event_hash CHECK (length(event_hash) = 64),
    CONSTRAINT ck_imported_event_outcome CHECK (outcome IN ('SUCCESS', 'REJECTED'))
) STRICT;
CREATE INDEX ix_imported_event_engagement ON imported_audit_event(import_id, engagement_id, source_sequence_no);

CREATE TRIGGER trg_client_import_immutable BEFORE UPDATE ON client_import FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-IMPORT-APPEND-ONLY: client imports are append-only'); END;
CREATE TRIGGER trg_client_import_no_delete BEFORE DELETE ON client_import FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-IMPORT-APPEND-ONLY: client imports cannot be deleted'); END;
CREATE TRIGGER trg_imported_event_immutable BEFORE UPDATE ON imported_audit_event FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-IMPORTED-AUDIT-APPEND-ONLY: imported audit history is append-only'); END;
CREATE TRIGGER trg_imported_event_no_delete BEFORE DELETE ON imported_audit_event FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-IMPORTED-AUDIT-APPEND-ONLY: imported audit history cannot be deleted'); END;

-- Imported attribution identities are inert forever.
CREATE TRIGGER trg_external_principal_insert_disabled BEFORE INSERT ON app_user FOR EACH ROW
WHEN NEW.is_external_principal = 1 AND (NEW.status <> 'DISABLED' OR NEW.is_local_demo <> 0)
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-EXTERNAL-PRINCIPAL: imported principals must be disabled and non-local'); END;
CREATE TRIGGER trg_external_principal_no_reactivate BEFORE UPDATE ON app_user FOR EACH ROW
WHEN OLD.is_external_principal = 1 AND (NEW.status <> 'DISABLED' OR NEW.is_external_principal <> 1)
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-EXTERNAL-PRINCIPAL: imported principals cannot be reactivated'); END;
CREATE TRIGGER trg_external_principal_no_membership BEFORE INSERT ON engagement_member FOR EACH ROW
WHEN (SELECT is_external_principal FROM app_user WHERE user_id = NEW.user_id) = 1
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-EXTERNAL-PRINCIPAL: imported principals cannot receive memberships'); END;
CREATE TRIGGER trg_external_principal_no_assignment BEFORE INSERT ON assignment FOR EACH ROW
WHEN (SELECT is_external_principal FROM app_user WHERE user_id = NEW.assignee_user_id) = 1
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-EXTERNAL-PRINCIPAL: imported principals cannot receive assignments'); END;
