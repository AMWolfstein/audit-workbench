-- Roll-forward foundation, detail page: the individual compared transactions.
-- Parameters: :previous_import_id, :current_import_id, :state (ALL or one state)
--             :page_size, :offset
--
-- State classification matches gl_snapshot_comparison_summary.sql exactly:
--   UNCHANGED          identical identity and identical digests
--   CHANGED_VALUE      same identity, different monetary values
--   CHANGED_ATTRIBUTES same identity and values, different account/date/description/reference
--   ADDED              present only in the current snapshot
--   REMOVED            present only in the previous snapshot
WITH prev AS (
    SELECT
        j.journal_identity AS journal_identity,
        l.line_identity    AS line_identity,
        l.line_hash        AS line_hash,
        l.value_hash       AS value_hash,
        l.account_code     AS account_code,
        l.debit_minor      AS debit_minor,
        l.credit_minor     AS credit_minor,
        l.transaction_date AS transaction_date,
        l.description      AS description,
        l.journal_source   AS journal_source
    FROM gl_line l
    JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id
    WHERE l.import_id = :previous_import_id
),
curr AS (
    SELECT
        j.journal_identity AS journal_identity,
        l.line_identity    AS line_identity,
        l.line_hash        AS line_hash,
        l.value_hash       AS value_hash,
        l.account_code     AS account_code,
        l.debit_minor      AS debit_minor,
        l.credit_minor     AS credit_minor,
        l.transaction_date AS transaction_date,
        l.description      AS description,
        l.journal_source   AS journal_source
    FROM gl_line l
    JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id
    WHERE l.import_id = :current_import_id
),
keys AS (
    SELECT journal_identity, line_identity FROM prev
    UNION
    SELECT journal_identity, line_identity FROM curr
),
compared AS (
    SELECT
        k.journal_identity AS journal_identity,
        k.line_identity    AS line_identity,
        CASE
            WHEN p.line_identity IS NULL THEN 'ADDED'
            WHEN c.line_identity IS NULL THEN 'REMOVED'
            WHEN p.line_hash = c.line_hash THEN 'UNCHANGED'
            WHEN p.value_hash <> c.value_hash THEN 'CHANGED_VALUE'
            ELSE 'CHANGED_ATTRIBUTES'
        END AS state,
        p.account_code     AS previous_account_code,
        c.account_code     AS current_account_code,
        p.debit_minor      AS previous_debit_minor,
        p.credit_minor     AS previous_credit_minor,
        c.debit_minor      AS current_debit_minor,
        c.credit_minor     AS current_credit_minor,
        p.transaction_date AS previous_transaction_date,
        c.transaction_date AS current_transaction_date,
        p.description      AS previous_description,
        c.description      AS current_description,
        p.journal_source   AS previous_journal_source,
        c.journal_source   AS current_journal_source
    FROM keys k
    LEFT JOIN prev p ON p.journal_identity = k.journal_identity AND p.line_identity = k.line_identity
    LEFT JOIN curr c ON c.journal_identity = k.journal_identity AND c.line_identity = k.line_identity
)
SELECT
    compared.*,
    COUNT(*) OVER () AS total_count
FROM compared
WHERE (:state = 'ALL' OR compared.state = :state)
ORDER BY
    CASE compared.state
        WHEN 'ADDED' THEN 1
        WHEN 'CHANGED_VALUE' THEN 2
        WHEN 'CHANGED_ATTRIBUTES' THEN 3
        WHEN 'REMOVED' THEN 4
        ELSE 5
    END,
    compared.journal_identity,
    compared.line_identity
LIMIT :page_size OFFSET :offset;
