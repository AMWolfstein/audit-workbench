-- Transactions that fall outside the financial period of one GL snapshot.
-- Parameters: :import_id, :page_size, :offset
--
-- Out-of-period transactions are imported and flagged, never moved or deleted.
-- This query is the exact list the auditor needs, with the source row number so
-- the finding can be traced back into the client file. It selects the same row
-- shape as gl_search.sql because both feed the same line mapper.
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
  AND l.is_out_of_period = 1
ORDER BY l.transaction_date, j.journal_identity, l.line_no
LIMIT :page_size OFFSET :offset;
