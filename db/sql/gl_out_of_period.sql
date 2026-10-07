-- Transactions that fall outside the financial period of one GL snapshot.
-- Parameters: :import_id, :page_size, :offset
--
-- Out-of-period transactions are imported and flagged, never moved or deleted.
-- This query is the exact list the auditor needs, with the source row number so
-- the finding can be traced back into the client file.
SELECT
    l.gl_line_id       AS gl_line_id,
    l.gl_journal_id    AS gl_journal_id,
    l.source_row_no    AS source_row_no,
    j.journal_number   AS journal_number,
    j.journal_identity AS journal_identity,
    l.line_identity    AS line_identity,
    l.account_code     AS account_code,
    l.transaction_date AS transaction_date,
    l.posting_date     AS posting_date,
    l.description      AS description,
    l.debit_minor      AS debit_minor,
    l.credit_minor     AS credit_minor,
    l.amount_minor     AS amount_minor,
    COUNT(*) OVER ()   AS total_count
FROM gl_line l
JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id
WHERE l.import_id = :import_id
  AND l.is_out_of_period = 1
ORDER BY l.transaction_date, j.journal_identity, l.line_no
LIMIT :page_size OFFSET :offset;
