-- Audit Workbench migration 0002 - integrity guards (database defence in depth)
--
-- These triggers implement enforcement layer 4/5 of ADR-007. The application
-- rejects protected writes with friendly errors first; these guards exist so a
-- missed code path, an ad-hoc script, or a future module cannot silently edit a
-- finalized engagement or rewrite history.
--
-- Error message convention: 'AWB-GUARD-<AREA>: <human readable reason>'.
-- The application maps the 'AWB-GUARD-' prefix to a safe user-facing message.

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------------
-- Company: archive instead of delete
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_company_no_delete
BEFORE DELETE ON company
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-COMPANY-DELETE: companies are archived, never deleted');
END;

-- ---------------------------------------------------------------------------
-- Financial year: a period definition in use may not be re-dated
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_financial_year_no_delete
BEFORE DELETE ON financial_year
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-YEAR-DELETE: financial year definitions are retained');
END;

CREATE TRIGGER trg_financial_year_immutable_when_used
BEFORE UPDATE ON financial_year
FOR EACH ROW
WHEN EXISTS (SELECT 1 FROM engagement e WHERE e.financial_year_id = OLD.financial_year_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-YEAR-IN-USE: a financial year used by an engagement cannot be changed');
END;

-- ---------------------------------------------------------------------------
-- Engagement: identity immutable, finalized terminal, transitions constrained
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_engagement_no_delete
BEFORE DELETE ON engagement
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ENGAGEMENT-DELETE: engagements are never deleted');
END;

-- Invariant 4: a year is never "advanced" by editing an engagement.
CREATE TRIGGER trg_engagement_identity_immutable
BEFORE UPDATE ON engagement
FOR EACH ROW
WHEN NEW.engagement_id <> OLD.engagement_id
    OR NEW.company_id <> OLD.company_id
    OR NEW.financial_year_id <> OLD.financial_year_id
    OR NEW.created_at_utc <> OLD.created_at_utc
    OR NEW.created_by <> OLD.created_by
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ENGAGEMENT-IDENTITY: company, financial year and creation metadata are immutable; create a new engagement instead');
END;

-- ADR-002: FINALIZED is terminal. No ordinary update of any column is allowed.
CREATE TRIGGER trg_engagement_finalized_terminal
BEFORE UPDATE ON engagement
FOR EACH ROW
WHEN OLD.status = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ENGAGEMENT-FINALIZED: a finalized engagement is read-only');
END;

-- Note: the FINALIZED case is excluded here so that an attempt to unfinalize
-- always reports the clearer trg_engagement_finalized_terminal message
-- (SQLite does not define the firing order of triggers on the same event).
CREATE TRIGGER trg_engagement_status_transition
BEFORE UPDATE OF status ON engagement
FOR EACH ROW
WHEN OLD.status <> 'FINALIZED' AND NOT (
       (OLD.status = 'DRAFT' AND NEW.status IN ('DRAFT', 'IN_PROGRESS', 'FINALIZED'))
    OR (OLD.status = 'IN_PROGRESS' AND NEW.status IN ('IN_PROGRESS', 'FINALIZED'))
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ENGAGEMENT-TRANSITION: unsupported engagement status transition');
END;

-- A finalization must be accompanied by a committed manifest row (same transaction).
CREATE TRIGGER trg_engagement_finalization_requires_manifest
BEFORE UPDATE OF status ON engagement
FOR EACH ROW
WHEN NEW.status = 'FINALIZED'
    AND NOT EXISTS (
        SELECT 1 FROM finalization_manifest m
        WHERE m.engagement_id = NEW.engagement_id
          AND m.root_digest = NEW.finalization_digest
    )
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINALIZATION-MANIFEST: finalization requires a matching manifest digest in the same transaction');
END;

-- ---------------------------------------------------------------------------
-- Account: year-owned, locked by finalization
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_account_insert_locked
BEFORE INSERT ON account
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-FINALIZED: cannot add accounts to a finalized engagement');
END;

CREATE TRIGGER trg_account_update_locked
BEFORE UPDATE ON account
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = OLD.engagement_id) = 'FINALIZED'
    OR (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-FINALIZED: cannot modify accounts of a finalized engagement');
END;

-- A row may never be moved between engagements (invariant 1/7).
CREATE TRIGGER trg_account_ownership_immutable
BEFORE UPDATE ON account
FOR EACH ROW
WHEN NEW.engagement_id <> OLD.engagement_id OR NEW.account_id <> OLD.account_id
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-OWNERSHIP: an account cannot be moved to another engagement');
END;

CREATE TRIGGER trg_account_delete_locked
BEFORE DELETE ON account
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = OLD.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-FINALIZED: cannot delete accounts of a finalized engagement');
END;

CREATE TRIGGER trg_account_delete_requires_no_values
BEFORE DELETE ON account
FOR EACH ROW
WHEN EXISTS (SELECT 1 FROM financial_data f WHERE f.account_id = OLD.account_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ACCOUNT-IN-USE: an account with recorded values cannot be deleted');
END;

-- ---------------------------------------------------------------------------
-- Financial data: append-only, year-owned, locked by finalization
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_financial_data_insert_locked
BEFORE INSERT ON financial_data
FOR EACH ROW
WHEN (SELECT e.status FROM engagement e WHERE e.engagement_id = NEW.engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-DATA-FINALIZED: cannot record values in a finalized engagement');
END;

-- ADR-008: corrections append a revision; existing rows are never updated.
CREATE TRIGGER trg_financial_data_append_only_update
BEFORE UPDATE ON financial_data
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY: financial values are append-only; record a new revision instead');
END;

CREATE TRIGGER trg_financial_data_append_only_delete
BEFORE DELETE ON financial_data
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY: financial values are append-only and cannot be deleted');
END;

-- Revision chains must stay inside one engagement and one account.
CREATE TRIGGER trg_financial_data_revision_chain
BEFORE INSERT ON financial_data
FOR EACH ROW
WHEN NEW.supersedes_id IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM financial_data p
        WHERE p.financial_data_id = NEW.supersedes_id
          AND p.engagement_id = NEW.engagement_id
          AND p.account_id = NEW.account_id
          AND p.revision_no = NEW.revision_no - 1
    )
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-DATA-REVISION: a revision must supersede the immediately previous revision of the same account');
END;

CREATE TRIGGER trg_financial_data_currency_matches_engagement
BEFORE INSERT ON financial_data
FOR EACH ROW
WHEN NEW.currency_code <> (SELECT e.currency_code FROM engagement e WHERE e.engagement_id = NEW.engagement_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-FINANCIAL-DATA-CURRENCY: value currency must match the engagement currency');
END;

-- ---------------------------------------------------------------------------
-- Prior-year relationship: validated at insert, immutable afterwards
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_prior_year_same_company
BEFORE INSERT ON prior_year_relationship
FOR EACH ROW
WHEN (SELECT c.company_id FROM engagement c WHERE c.engagement_id = NEW.current_engagement_id)
     <> (SELECT p.company_id FROM engagement p WHERE p.engagement_id = NEW.prior_engagement_id)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-COMPANY: the prior engagement must belong to the same company');
END;

CREATE TRIGGER trg_prior_year_must_be_finalized
BEFORE INSERT ON prior_year_relationship
FOR EACH ROW
WHEN (SELECT p.status FROM engagement p WHERE p.engagement_id = NEW.prior_engagement_id) <> 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-STATUS: the prior engagement must be finalized');
END;

CREATE TRIGGER trg_prior_year_must_be_earlier
BEFORE INSERT ON prior_year_relationship
FOR EACH ROW
WHEN (
        SELECT fy.period_end FROM engagement p
        JOIN financial_year fy ON fy.financial_year_id = p.financial_year_id
        WHERE p.engagement_id = NEW.prior_engagement_id
     ) >= (
        SELECT fy.period_end FROM engagement c
        JOIN financial_year fy ON fy.financial_year_id = c.financial_year_id
        WHERE c.engagement_id = NEW.current_engagement_id
     )
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-PERIOD: the prior engagement period must end before the current period');
END;

CREATE TRIGGER trg_prior_year_current_not_finalized
BEFORE INSERT ON prior_year_relationship
FOR EACH ROW
WHEN (SELECT c.status FROM engagement c WHERE c.engagement_id = NEW.current_engagement_id) = 'FINALIZED'
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-FINALIZED: a finalized engagement cannot gain a prior-year link');
END;

CREATE TRIGGER trg_prior_year_no_cycle
BEFORE INSERT ON prior_year_relationship
FOR EACH ROW
WHEN EXISTS (
    SELECT 1 FROM prior_year_relationship r
    WHERE r.current_engagement_id = NEW.prior_engagement_id
      AND r.prior_engagement_id = NEW.current_engagement_id
)
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-CYCLE: prior-year relationships must not form a cycle');
END;

CREATE TRIGGER trg_prior_year_immutable
BEFORE UPDATE ON prior_year_relationship
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-IMMUTABLE: a prior-year relationship cannot be changed');
END;

CREATE TRIGGER trg_prior_year_no_delete
BEFORE DELETE ON prior_year_relationship
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-PRIOR-YEAR-IMMUTABLE: a prior-year relationship cannot be removed');
END;

-- ---------------------------------------------------------------------------
-- Finalization manifest and audit trail: append-only evidence
-- ---------------------------------------------------------------------------

CREATE TRIGGER trg_manifest_immutable
BEFORE UPDATE ON finalization_manifest
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MANIFEST-IMMUTABLE: a finalization manifest cannot be changed');
END;

CREATE TRIGGER trg_manifest_no_delete
BEFORE DELETE ON finalization_manifest
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-MANIFEST-IMMUTABLE: a finalization manifest cannot be deleted');
END;

CREATE TRIGGER trg_audit_event_immutable
BEFORE UPDATE ON audit_event
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-AUDIT-APPEND-ONLY: audit events are append-only');
END;

CREATE TRIGGER trg_audit_event_no_delete
BEFORE DELETE ON audit_event
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-AUDIT-APPEND-ONLY: audit events cannot be deleted');
END;
