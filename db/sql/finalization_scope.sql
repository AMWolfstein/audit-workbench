-- In-scope content of one engagement at finalization time, in canonical order.
-- Parameters: :engagement_id
--
-- The result feeds the canonical manifest document (docs/finalization-manifest.md).
-- Ordering is by account_code so the digest is deterministic and independent of
-- insertion order or SQLite physical layout.
SELECT
    a.account_code      AS account_code,
    a.account_name      AS account_name,
    a.account_type      AS account_type,
    f.revision_no       AS revision_no,
    f.amount_minor      AS amount_minor,
    f.currency_code     AS currency_code
FROM account a
LEFT JOIN financial_data f
       ON f.account_id = a.account_id
      AND f.engagement_id = a.engagement_id
      AND f.revision_no = (
            SELECT MAX(x.revision_no)
            FROM financial_data x
            WHERE x.engagement_id = a.engagement_id
              AND x.account_id = a.account_id)
WHERE a.engagement_id = :engagement_id
ORDER BY a.account_code;
