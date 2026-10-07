-- Search imported general-ledger lines of exactly one snapshot.
-- Parameters: :import_id, :transaction_id, :account_code, :date_from, :date_to,
--             :min_amount, :max_amount, :description, :journal_source, :reference,
--             :out_of_period_only, :page_size, :offset
--
-- Filters are optional: a NULL parameter means "no filter". Paging happens in SQL
-- and the total match count is returned with the page.
SELECT
    l.gl_line_id           AS gl_line_id,
    l.gl_journal_id        AS gl_journal_id,
    l.line_no              AS line_no,
    l.source_row_no        AS source_row_no,
    j.journal_identity     AS journal_identity,
    j.journal_number       AS journal_number,
    l.line_identity        AS line_identity,
    l.source_line_no       AS source_line_no,
    l.account_code         AS account_code,
    l.account_name         AS account_name,
    l.transaction_date     AS transaction_date,
    l.posting_date         AS posting_date,
    l.description          AS description,
    l.debit_minor          AS debit_minor,
    l.credit_minor         AS credit_minor,
    l.amount_minor         AS amount_minor,
    l.currency_code        AS currency_code,
    l.journal_source       AS journal_source,
    l.reference            AS reference,
    l.prepared_by          AS prepared_by,
    l.is_out_of_period     AS is_out_of_period,
    ac.audit_area_id       AS audit_area_id,
    COUNT(*) OVER ()       AS total_count
FROM gl_line l
JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id
LEFT JOIN account ac ON ac.account_id = l.account_id AND ac.engagement_id = l.engagement_id
WHERE l.import_id = :import_id
  AND (:transaction_id IS NULL
       OR j.journal_identity = :transaction_id
       OR j.journal_number LIKE :transaction_id
       OR l.line_identity = :transaction_id
       OR l.reference LIKE :transaction_id)
  AND (:account_code IS NULL OR l.account_code LIKE :account_code)
  AND (:date_from IS NULL OR l.transaction_date >= :date_from)
  AND (:date_to IS NULL OR l.transaction_date <= :date_to)
  AND (:min_amount IS NULL OR l.amount_minor >= :min_amount)
  AND (:max_amount IS NULL OR l.amount_minor <= :max_amount)
  AND (:description IS NULL OR lower(l.description) LIKE :description)
  AND (:journal_source IS NULL OR lower(coalesce(l.journal_source, '')) LIKE :journal_source)
  AND (:reference IS NULL OR lower(coalesce(l.reference, '')) LIKE :reference)
  AND (:out_of_period_only = 0 OR l.is_out_of_period = 1)
ORDER BY l.transaction_date, j.journal_identity, l.line_no
LIMIT :page_size OFFSET :offset;
