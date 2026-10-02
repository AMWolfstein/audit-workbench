-- Comparative values between a current engagement and its linked prior engagement.
-- Parameters: :current_engagement_id, :prior_engagement_id
--
-- The query is strictly read-only and resolves each year independently
-- (data-model.md section 5). Accounts are matched on stable account_code.
-- Change amount and percentage are calculated by the application so that
-- rounding and the "prior is zero -> N/A" rule stay in one tested place.
WITH latest AS (
    SELECT
        fd.engagement_id,
        fd.account_id,
        fd.amount_minor,
        fd.revision_no
    FROM financial_data fd
    WHERE fd.engagement_id IN (:current_engagement_id, :prior_engagement_id)
      AND fd.revision_no = (
            SELECT MAX(x.revision_no)
            FROM financial_data x
            WHERE x.engagement_id = fd.engagement_id
              AND x.account_id = fd.account_id)
),
cur AS (
    SELECT a.account_code, a.account_name, a.display_order, l.amount_minor, l.revision_no
    FROM account a
    LEFT JOIN latest l ON l.account_id = a.account_id AND l.engagement_id = a.engagement_id
    WHERE a.engagement_id = :current_engagement_id
),
pri AS (
    SELECT a.account_code, a.account_name, a.display_order, l.amount_minor, l.revision_no
    FROM account a
    LEFT JOIN latest l ON l.account_id = a.account_id AND l.engagement_id = a.engagement_id
    WHERE a.engagement_id = :prior_engagement_id
),
codes AS (
    SELECT account_code FROM cur
    UNION
    SELECT account_code FROM pri
)
SELECT
    codes.account_code                                        AS account_code,
    COALESCE(cur.account_name, pri.account_name)              AS account_name,
    pri.amount_minor                                          AS prior_amount_minor,
    cur.amount_minor                                          AS current_amount_minor,
    pri.revision_no                                           AS prior_revision_no,
    cur.revision_no                                           AS current_revision_no,
    CASE WHEN pri.account_code IS NULL THEN 1 ELSE 0 END      AS is_new_account,
    CASE WHEN cur.account_code IS NULL THEN 1 ELSE 0 END      AS is_missing_in_current
FROM codes
LEFT JOIN cur ON cur.account_code = codes.account_code
LEFT JOIN pri ON pri.account_code = codes.account_code
ORDER BY COALESCE(cur.display_order, pri.display_order), codes.account_code;
