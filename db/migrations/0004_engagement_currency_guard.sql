-- Audit Workbench migration 0004 - currency and minor-unit scale are part of an engagement's identity
--
-- Amounts are stored as integer minor units interpreted through the engagement's
-- currency_code and minor_unit_scale. Editing either after creation would silently
-- reinterpret every stored amount (and the finalization manifest), so, like company and
-- financial year (migration 0002), they can never change. Create a new engagement instead.
-- Added as a new migration because applied migrations are checksummed and never edited.

CREATE TRIGGER trg_engagement_currency_immutable
BEFORE UPDATE ON engagement
FOR EACH ROW
WHEN NEW.currency_code <> OLD.currency_code
    OR NEW.minor_unit_scale <> OLD.minor_unit_scale
BEGIN
    SELECT RAISE(ABORT, 'AWB-GUARD-ENGAGEMENT-IDENTITY: currency and minor-unit scale are immutable because stored amounts depend on them; create a new engagement instead');
END;
