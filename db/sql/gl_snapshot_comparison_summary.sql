-- Roll-forward foundation: compare two GL snapshots of the same period.
-- Parameters: :previous_import_id, :current_import_id
--
-- Transactions are matched on the client's identity (journal identity + line
-- identity), never on database ids. Every compared line is classified as
-- UNCHANGED, CHANGED_VALUE, CHANGED_ATTRIBUTES, ADDED or REMOVED; changed and
-- added lines will later be eligible for mandatory sampling.
WITH prev AS (
    SELECT
        j.journal_identity AS journal_identity,
        l.line_identity    AS line_identity,
        l.line_hash        AS line_hash,
        l.value_hash       AS value_hash,
        l.debit_minor      AS debit_minor,
        l.credit_minor     AS credit_minor
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
        l.debit_minor      AS debit_minor,
        l.credit_minor     AS credit_minor
    FROM gl_line l
    JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id
    WHERE l.import_id = :current_import_id
),
keys AS (
    SELECT journal_identity, line_identity FROM prev
    UNION
    SELECT journal_identity, line_identity FROM curr
)
SELECT
    COALESCE(SUM(CASE WHEN p.line_identity IS NULL THEN 1 ELSE 0 END), 0) AS added_count,
    COALESCE(SUM(CASE WHEN c.line_identity IS NULL THEN 1 ELSE 0 END), 0) AS removed_count,
    COALESCE(SUM(CASE WHEN p.line_identity IS NOT NULL AND c.line_identity IS NOT NULL
                       AND p.line_hash = c.line_hash THEN 1 ELSE 0 END), 0) AS unchanged_count,
    COALESCE(SUM(CASE WHEN p.line_identity IS NOT NULL AND c.line_identity IS NOT NULL
                       AND p.line_hash <> c.line_hash AND p.value_hash <> c.value_hash
                 THEN 1 ELSE 0 END), 0) AS changed_value_count,
    COALESCE(SUM(CASE WHEN p.line_identity IS NOT NULL AND c.line_identity IS NOT NULL
                       AND p.line_hash <> c.line_hash AND p.value_hash = c.value_hash
                 THEN 1 ELSE 0 END), 0) AS changed_attributes_count,
    COALESCE(SUM(CASE WHEN p.line_identity IS NULL THEN c.debit_minor ELSE 0 END), 0) AS added_debit_minor,
    COALESCE(SUM(CASE WHEN p.line_identity IS NULL THEN c.credit_minor ELSE 0 END), 0) AS added_credit_minor,
    -- The true movement of a changed transaction: the current net minus the net
    -- that was carried in the previous snapshot (an attribute-only change has the
    -- same net and therefore contributes nothing here).
    COALESCE(SUM(CASE WHEN p.line_identity IS NOT NULL AND c.line_identity IS NOT NULL
                       AND p.line_hash <> c.line_hash
                 THEN (c.debit_minor - c.credit_minor) - (p.debit_minor - p.credit_minor)
                 ELSE 0 END), 0) AS changed_value_delta_minor,
    COALESCE(SUM(CASE WHEN c.line_identity IS NULL THEN p.debit_minor ELSE 0 END), 0) AS removed_debit_minor,
    COALESCE(SUM(CASE WHEN c.line_identity IS NULL THEN p.credit_minor ELSE 0 END), 0) AS removed_credit_minor
FROM keys k
LEFT JOIN prev p ON p.journal_identity = k.journal_identity AND p.line_identity = k.line_identity
LEFT JOIN curr c ON c.journal_identity = k.journal_identity AND c.line_identity = k.line_identity;
