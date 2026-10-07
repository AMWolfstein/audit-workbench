-- Search imported trial-balance lines of exactly one import.
-- Parameters: :import_id, :account_code, :account_name, :account_group,
--             :min_balance, :max_balance, :page_size, :offset
--
-- Filters are optional: a NULL parameter means "no filter". The query is paged in
-- SQL (never in the page model) and returns the total match count alongside the
-- page so the UI can show "n of m".
SELECT
    t.tb_line_id        AS tb_line_id,
    t.line_no           AS line_no,
    t.source_row_no     AS source_row_no,
    t.account_code      AS account_code,
    t.account_name      AS account_name,
    t.account_group     AS account_group,
    t.cost_center       AS cost_center,
    t.debit_minor       AS debit_minor,
    t.credit_minor      AS credit_minor,
    t.balance_minor     AS balance_minor,
    t.currency_code     AS currency_code,
    a.audit_area_id     AS audit_area_id,
    aa.area_code        AS audit_area_code,
    COUNT(*) OVER ()    AS total_count
FROM tb_line t
JOIN account a ON a.account_id = t.account_id AND a.engagement_id = t.engagement_id
LEFT JOIN audit_area aa ON aa.audit_area_id = a.audit_area_id
WHERE t.import_id = :import_id
  AND (:account_code IS NULL OR t.account_code LIKE :account_code OR t.normalized_code LIKE :account_code)
  AND (:account_name IS NULL OR lower(t.account_name) LIKE :account_name)
  AND (:account_group IS NULL OR lower(coalesce(t.account_group, '')) LIKE :account_group)
  AND (:min_balance IS NULL OR t.balance_minor >= :min_balance)
  AND (:max_balance IS NULL OR t.balance_minor <= :max_balance)
ORDER BY t.line_no
LIMIT :page_size OFFSET :offset;
