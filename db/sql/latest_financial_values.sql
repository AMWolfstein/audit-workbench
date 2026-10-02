-- Latest (highest revision) value per account for exactly one engagement.
-- Parameters: :engagement_id
--
-- "Current" is derived from the highest revision_no; there is no mutable
-- is_current flag (data-model.md section 4).
SELECT
    a.account_id            AS account_id,
    a.account_code          AS account_code,
    a.account_name          AS account_name,
    a.account_type          AS account_type,
    a.display_order         AS display_order,
    f.financial_data_id     AS financial_data_id,
    f.revision_no           AS revision_no,
    f.amount_minor          AS amount_minor,
    f.currency_code         AS currency_code,
    f.recorded_at_utc       AS recorded_at_utc,
    f.recorded_by           AS recorded_by,
    u.display_name          AS recorded_by_display_name,
    (SELECT COUNT(*) FROM financial_data h
      WHERE h.engagement_id = a.engagement_id AND h.account_id = a.account_id) AS revision_count
FROM account a
LEFT JOIN financial_data f
       ON f.account_id = a.account_id
      AND f.engagement_id = a.engagement_id
      AND f.revision_no = (
            SELECT MAX(x.revision_no)
            FROM financial_data x
            WHERE x.engagement_id = a.engagement_id
              AND x.account_id = a.account_id)
LEFT JOIN app_user u ON u.user_id = f.recorded_by
WHERE a.engagement_id = :engagement_id
ORDER BY a.display_order, a.account_code;
