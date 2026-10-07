-- Audit Workbench migration 0006 - close team ownership integrity gaps
-- SQLite-specific guards corresponding to provider-neutral application invariants.

CREATE INDEX ix_engagement_member_engagement_status
    ON engagement_member(engagement_id, status, role_id);
CREATE INDEX ix_assignment_engagement_status
    ON assignment(engagement_id, status);

-- Membership identity is historical identity. Role/status/version may change, but a
-- membership can never be moved to another person or engagement.
CREATE TRIGGER trg_member_identity_immutable BEFORE UPDATE ON engagement_member FOR EACH ROW
WHEN NEW.engagement_id <> OLD.engagement_id
  OR NEW.user_id <> OLD.user_id
  OR NEW.engagement_member_id <> OLD.engagement_member_id
  OR NEW.added_by <> OLD.added_by
  OR NEW.added_at_utc <> OLD.added_at_utc
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MEMBER-IDENTITY: membership identity and origin are immutable');
END;

-- Assignment ownership and scope identify the historical work item. Reassignment,
-- state and version may change, but moving it across engagements/scopes is forbidden.
CREATE TRIGGER trg_assignment_identity_immutable BEFORE UPDATE ON assignment FOR EACH ROW
WHEN NEW.assignment_id <> OLD.assignment_id
  OR NEW.engagement_id <> OLD.engagement_id
  OR NEW.scope_type <> OLD.scope_type
  OR NEW.scope_id <> OLD.scope_id
  OR NEW.assigned_by <> OLD.assigned_by
  OR NEW.assigned_at_utc <> OLD.assigned_at_utc
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-IDENTITY: assignment ownership, scope and origin are immutable');
END;

-- Defense in depth for application validation: assignments can only target active
-- members whose application account is active. This also applies to reassignment.
CREATE TRIGGER trg_assignment_active_member_insert BEFORE INSERT ON assignment FOR EACH ROW
WHEN COALESCE((SELECT is_external_principal FROM app_user WHERE user_id = NEW.assignee_user_id), 0) = 0
 AND NOT EXISTS (
    SELECT 1
      FROM engagement_member m
      JOIN app_user u ON u.user_id = m.user_id
     WHERE m.engagement_id = NEW.engagement_id
       AND m.user_id = NEW.assignee_user_id
       AND m.status = 'ACTIVE'
       AND u.status = 'ACTIVE'
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-MEMBER: assignee must be an active engagement member');
END;

CREATE TRIGGER trg_assignment_active_member_update BEFORE UPDATE OF assignee_user_id ON assignment FOR EACH ROW
WHEN COALESCE((SELECT is_external_principal FROM app_user WHERE user_id = NEW.assignee_user_id), 0) = 0
 AND NOT EXISTS (
    SELECT 1
      FROM engagement_member m
      JOIN app_user u ON u.user_id = m.user_id
     WHERE m.engagement_id = NEW.engagement_id
       AND m.user_id = NEW.assignee_user_id
       AND m.status = 'ACTIVE'
       AND u.status = 'ACTIVE'
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ASSIGNMENT-MEMBER: assignee must be an active engagement member');
END;
