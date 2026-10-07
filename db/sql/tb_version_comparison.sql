-- Compare two trial-balance versions of one period, account by account.
-- Parameters: :previous_import_id, :current_import_id
--
-- A TB version is a new file, not an edited one, so the comparison is the same
-- identity-based comparison used for GL snapshots: unchanged, changed balance,
-- changed attributes (debit/credit split), added and removed accounts.
WITH prev AS (
    SELECT
        account_code   AS account_code,
        account_name   AS account_name,
        debit_minor    AS debit_minor,
        credit_minor   AS credit_minor,
        balance_minor  AS balance_minor,
        row_hash       AS row_hash
    FROM tb_line
    WHERE import_id = :previous_import_id
),
curr AS (
    SELECT
        account_code   AS account_code,
        account_name   AS account_name,
        debit_minor    AS debit_minor,
        credit_minor   AS credit_minor,
        balance_minor  AS balance_minor,
        row_hash       AS row_hash
    FROM tb_line
    WHERE import_id = :current_import_id
),
keys AS (
    SELECT account_code FROM prev
    UNION
    SELECT account_code FROM curr
)
SELECT
    CASE
        WHEN p.account_code IS NULL THEN 'ADDED'
        WHEN c.account_code IS NULL THEN 'REMOVED'
        WHEN p.row_hash = c.row_hash THEN 'UNCHANGED'
        WHEN p.balance_minor <> c.balance_minor THEN 'CHANGED_VALUE'
        ELSE 'CHANGED_ATTRIBUTES'
    END AS state,
    k.account_code AS account_code,
    COALESCE(c.account_name, p.account_name, '') AS account_name,
    p.balance_minor AS previous_balance_minor,
    c.balance_minor AS current_balance_minor,
    p.debit_minor   AS previous_debit_minor,
    p.credit_minor  AS previous_credit_minor,
    c.debit_minor   AS current_debit_minor,
    c.credit_minor  AS current_credit_minor
FROM keys k
LEFT JOIN prev p ON p.account_code = k.account_code
LEFT JOIN curr c ON c.account_code = k.account_code
ORDER BY k.account_code;
