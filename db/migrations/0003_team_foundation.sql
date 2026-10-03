-- Audit Workbench migration 0003 - team-first identity, authorization and assignments
-- SQLite representation of provider-independent concepts. Central-provider migrations
-- must preserve these constraints using provider-native DDL.

ALTER TABLE app_user ADD COLUMN email TEXT NULL;
ALTER TABLE app_user ADD COLUMN updated_at_utc TEXT NULL;

CREATE TABLE app_role (
    role_id        TEXT NOT NULL PRIMARY KEY,
    role_key       TEXT NOT NULL,
    display_name   TEXT NOT NULL,
    is_system      INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    CONSTRAINT ck_role_key CHECK (role_key = upper(trim(role_key)) AND length(role_key) > 0),
    CONSTRAINT ck_role_system CHECK (is_system IN (0, 1))
) STRICT;
CREATE UNIQUE INDEX ux_app_role_key ON app_role(role_key);

CREATE TABLE role_permission (
    role_id        TEXT NOT NULL,
    permission_key TEXT NOT NULL,
    CONSTRAINT pk_role_permission PRIMARY KEY (role_id, permission_key),
    CONSTRAINT fk_role_permission_role FOREIGN KEY (role_id) REFERENCES app_role(role_id),
    CONSTRAINT ck_permission_key CHECK (permission_key = upper(trim(permission_key)) AND length(permission_key) > 0)
) STRICT;

CREATE TABLE engagement_member (
    engagement_member_id TEXT NOT NULL PRIMARY KEY,
    engagement_id        TEXT NOT NULL,
    user_id               TEXT NOT NULL,
    role_id               TEXT NOT NULL,
    status                TEXT NOT NULL DEFAULT 'ACTIVE',
    added_at_utc          TEXT NOT NULL,
    added_by              TEXT NOT NULL,
    updated_at_utc        TEXT NOT NULL,
    row_version           INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT fk_member_engagement FOREIGN KEY (engagement_id) REFERENCES engagement(engagement_id),
    CONSTRAINT fk_member_user FOREIGN KEY (user_id) REFERENCES app_user(user_id),
    CONSTRAINT fk_member_role FOREIGN KEY (role_id) REFERENCES app_role(role_id),
    CONSTRAINT fk_member_added_by FOREIGN KEY (added_by) REFERENCES app_user(user_id),
    CONSTRAINT ck_member_status CHECK (status IN ('ACTIVE', 'SUSPENDED', 'REMOVED')),
    CONSTRAINT ck_member_row_version CHECK (row_version > 0)
) STRICT;
CREATE UNIQUE INDEX ux_engagement_member_user ON engagement_member(engagement_id, user_id);
CREATE INDEX ix_engagement_member_access ON engagement_member(user_id, engagement_id, status);

CREATE TABLE assignment (
    assignment_id     TEXT NOT NULL PRIMARY KEY,
    engagement_id     TEXT NOT NULL,
    assignee_user_id  TEXT NOT NULL,
    scope_type        TEXT NOT NULL,
    scope_id          TEXT NOT NULL,
    title             TEXT NOT NULL,
    status            TEXT NOT NULL DEFAULT 'ACTIVE',
    assigned_at_utc   TEXT NOT NULL,
    assigned_by       TEXT NOT NULL,
    updated_at_utc    TEXT NOT NULL,
    row_version       INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT fk_assignment_engagement FOREIGN KEY (engagement_id) REFERENCES engagement(engagement_id),
    CONSTRAINT fk_assignment_assignee FOREIGN KEY (assignee_user_id) REFERENCES app_user(user_id),
    CONSTRAINT fk_assignment_assigned_by FOREIGN KEY (assigned_by) REFERENCES app_user(user_id),
    CONSTRAINT ck_assignment_scope CHECK (scope_type IN ('ENGAGEMENT', 'AUDIT_AREA', 'WORKING_PAPER', 'PROCEDURE')),
    CONSTRAINT ck_assignment_status CHECK (status IN ('ACTIVE', 'COMPLETED', 'CANCELLED')),
    CONSTRAINT ck_assignment_title CHECK (length(trim(title)) > 0),
    CONSTRAINT ck_assignment_row_version CHECK (row_version > 0)
) STRICT;
CREATE UNIQUE INDEX ux_assignment_scope_user ON assignment(engagement_id, scope_type, scope_id, assignee_user_id);
CREATE INDEX ix_assignment_assignee ON assignment(assignee_user_id, status);

-- Stable seed identifiers are infrastructure data, not domain assumptions. Roles and
-- permission mappings may be changed through future administration/governance.
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000001','PARTNER','Partner',1,'1970-01-01T00:00:00.000Z');
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000002','MANAGER','Manager',1,'1970-01-01T00:00:00.000Z');
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000003','SENIOR','Senior',1,'1970-01-01T00:00:00.000Z');
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000004','AUDITOR','Auditor',1,'1970-01-01T00:00:00.000Z');
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000005','REVIEWER','Reviewer',1,'1970-01-01T00:00:00.000Z');
INSERT INTO app_role VALUES ('10000000-0000-4000-8000-000000000006','READ_ONLY','Read only',1,'1970-01-01T00:00:00.000Z');

INSERT INTO role_permission
SELECT role_id, permission_key FROM app_role CROSS JOIN (
 SELECT 'VIEW_ENGAGEMENT' permission_key UNION ALL SELECT 'EDIT_ENGAGEMENT' UNION ALL SELECT 'MANAGE_TEAM'
 UNION ALL SELECT 'MANAGE_ASSIGNMENTS' UNION ALL SELECT 'EDIT_WORKING_PAPERS' UNION ALL SELECT 'UPLOAD_EVIDENCE'
 UNION ALL SELECT 'REVIEW_WORKING_PAPERS' UNION ALL SELECT 'CLEAR_REVIEW_NOTES' UNION ALL SELECT 'FINALIZE_ENGAGEMENT'
 UNION ALL SELECT 'EXPORT_ENGAGEMENT' UNION ALL SELECT 'IMPORT_ENGAGEMENT' UNION ALL SELECT 'VIEW_AUDIT_TRAIL'
) WHERE role_key = 'PARTNER';
INSERT INTO role_permission SELECT role_id, 'VIEW_ENGAGEMENT' FROM app_role WHERE role_key <> 'PARTNER';
INSERT INTO role_permission SELECT role_id, 'EDIT_ENGAGEMENT' FROM app_role WHERE role_key IN ('MANAGER','SENIOR','AUDITOR');
INSERT INTO role_permission SELECT role_id, 'MANAGE_TEAM' FROM app_role WHERE role_key = 'MANAGER';
INSERT INTO role_permission SELECT role_id, 'MANAGE_ASSIGNMENTS' FROM app_role WHERE role_key IN ('MANAGER','SENIOR');
INSERT INTO role_permission SELECT role_id, 'EDIT_WORKING_PAPERS' FROM app_role WHERE role_key IN ('MANAGER','SENIOR','AUDITOR');
INSERT INTO role_permission SELECT role_id, 'UPLOAD_EVIDENCE' FROM app_role WHERE role_key IN ('MANAGER','SENIOR','AUDITOR');
INSERT INTO role_permission SELECT role_id, 'REVIEW_WORKING_PAPERS' FROM app_role WHERE role_key IN ('MANAGER','SENIOR','REVIEWER');
INSERT INTO role_permission SELECT role_id, 'CLEAR_REVIEW_NOTES' FROM app_role WHERE role_key IN ('MANAGER','REVIEWER');
INSERT INTO role_permission SELECT role_id, 'FINALIZE_ENGAGEMENT' FROM app_role WHERE role_key = 'MANAGER';
INSERT INTO role_permission SELECT role_id, 'EXPORT_ENGAGEMENT' FROM app_role WHERE role_key IN ('MANAGER','READ_ONLY');
INSERT INTO role_permission SELECT role_id, 'VIEW_AUDIT_TRAIL' FROM app_role WHERE role_key IN ('MANAGER','SENIOR','REVIEWER','READ_ONLY');

-- Membership and assignments belong to the year and are immutable after finalization.
CREATE TRIGGER trg_member_insert_locked BEFORE INSERT ON engagement_member FOR EACH ROW
WHEN (SELECT status FROM engagement WHERE engagement_id=NEW.engagement_id)='FINALIZED'
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-MEMBER-FINALIZED: cannot change the team of a finalized engagement'); END;
CREATE TRIGGER trg_member_update_locked BEFORE UPDATE ON engagement_member FOR EACH ROW
WHEN (SELECT status FROM engagement WHERE engagement_id=OLD.engagement_id)='FINALIZED'
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-MEMBER-FINALIZED: cannot change the team of a finalized engagement'); END;
CREATE TRIGGER trg_member_delete_locked BEFORE DELETE ON engagement_member FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-MEMBER-DELETE: membership history cannot be deleted'); END;
CREATE TRIGGER trg_assignment_insert_locked BEFORE INSERT ON assignment FOR EACH ROW
WHEN (SELECT status FROM engagement WHERE engagement_id=NEW.engagement_id)='FINALIZED'
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-FINALIZED: cannot assign work in a finalized engagement'); END;
CREATE TRIGGER trg_assignment_update_locked BEFORE UPDATE ON assignment FOR EACH ROW
WHEN (SELECT status FROM engagement WHERE engagement_id=OLD.engagement_id)='FINALIZED'
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-FINALIZED: cannot change assignments in a finalized engagement'); END;
CREATE TRIGGER trg_assignment_delete_no BEFORE DELETE ON assignment FOR EACH ROW
BEGIN SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-DELETE: assignment history cannot be deleted'); END;
