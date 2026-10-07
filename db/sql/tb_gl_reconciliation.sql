-- TB <-> GL reconciliation for one trial-balance version and one ledger snapshot.
-- Parameters: :tb_import_id (nullable), :gl_import_id (nullable)
--
-- Sign convention: debit-positive. tb_balance_minor = debit - credit and
-- gl_net_minor = sum(debit) - sum(credit), so a credit balance is negative.
-- Accounts present in only one dataset are returned with NULLs on the other side:
-- differences are never hidden. The comparison status is decided by the
-- application from these values.
WITH tb AS (
    SELECT
        t.account_code      AS account_code,
        MAX(t.account_name) AS account_name,
        SUM(t.debit_minor)  AS tb_debit_minor,
        SUM(t.credit_minor) AS tb_credit_minor,
        SUM(t.balance_minor) AS tb_balance_minor
    FROM tb_line t
    WHERE t.import_id = :tb_import_id
    GROUP BY t.account_code
),
gl AS (
    SELECT
        l.account_code                  AS account_code,
        SUM(l.debit_minor)              AS gl_debit_minor,
        SUM(l.credit_minor)             AS gl_credit_minor,
        SUM(l.amount_minor)             AS gl_net_minor,
        COUNT(*)                        AS gl_line_count,
        SUM(l.is_out_of_period)         AS gl_out_of_period_count
    FROM gl_line l
    WHERE l.import_id = :gl_import_id
    GROUP BY l.account_code
),
keys AS (
    SELECT account_code FROM tb
    UNION
    SELECT account_code FROM gl
)
SELECT
    k.account_code              AS account_code,
    COALESCE(tb.account_name, '') AS account_name,
    tb.tb_debit_minor           AS tb_debit_minor,
    tb.tb_credit_minor          AS tb_credit_minor,
    tb.tb_balance_minor         AS tb_balance_minor,
    gl.gl_debit_minor           AS gl_debit_minor,
    gl.gl_credit_minor          AS gl_credit_minor,
    gl.gl_net_minor             AS gl_net_minor,
    COALESCE(gl.gl_line_count, 0)          AS gl_line_count,
    COALESCE(gl.gl_out_of_period_count, 0) AS gl_out_of_period_count
FROM keys k
LEFT JOIN tb ON tb.account_code = k.account_code
LEFT JOIN gl ON gl.account_code = k.account_code
ORDER BY k.account_code;
