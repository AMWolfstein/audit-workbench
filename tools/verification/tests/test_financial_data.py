"""Migration 0007: financial periods, TB/GL imports, versioning and their guards.

These tests execute the shipped SQL, not a model of it: every assertion is made
against a real on-disk SQLite workspace with the migrations applied, and the
read queries in ``db/sql`` are executed exactly as the application executes them.
"""

from __future__ import annotations

import sqlite3
import unittest
import uuid
from pathlib import Path

from _harness import REPO_ROOT, WorkspaceTestCase

MIGRATION_0007 = REPO_ROOT / "db" / "migrations" / "0007_financial_data_foundation.sql"
QUERY_DIR = REPO_ROOT / "db" / "sql"
ACTOR = "00000000-0000-4000-8000-000000000001"
STAMP = "2027-01-01T00:00:00.000Z"


def new_id() -> str:
    return str(uuid.uuid4())


def digest(character: str) -> str:
    return character * 64


def load_query(name: str) -> str:
    return (QUERY_DIR / name).read_text(encoding="utf-8")


def backfill_statement() -> str:
    """The exact period-backfill statement of migration 0007."""
    script = MIGRATION_0007.read_text(encoding="utf-8")
    start = script.index("INSERT INTO financial_period (")
    end = script.index("CREATE TRIGGER trg_financial_period_no_delete")
    return script[start:end].strip()


class FinancialDataTestCase(WorkspaceTestCase):
    """Shared builders for the financial-data tables of migration 0007."""

    def setUp(self) -> None:
        super().setUp()
        self.company_id = self.create_company()
        self.engagement_id = self.create_year(self.company_id, "FY2026", 2026)

    # -- builders ----------------------------------------------------------

    def add_period(self, engagement_id: str | None = None, status: str = "OPEN",
                   reporting_date: str = "2026-12-31") -> str:
        engagement_id = engagement_id or self.engagement_id
        period_id = new_id()
        self.raw_sql(
            "INSERT INTO financial_period (financial_period_id, engagement_id, financial_year_id, "
            "reporting_date, status, created_at_utc, created_by, updated_at_utc, row_version) "
            "SELECT ?, engagement_id, financial_year_id, ?, ?, ?, ?, ?, 1 FROM engagement "
            "WHERE engagement_id = ?",
            (period_id, reporting_date, status, STAMP, ACTOR, STAMP, engagement_id),
        )
        return period_id

    def add_upload(self, engagement_id: str | None = None, kind: str = "TB",
                   sha256: str | None = None, file_name: str = "trial-balance.csv") -> str:
        engagement_id = engagement_id or self.engagement_id
        upload_id = new_id()
        sha256 = sha256 or digest("a")
        self.raw_sql(
            "INSERT INTO financial_upload (upload_id, engagement_id, dataset_kind, file_name, content_type, "
            "size_bytes, sha256, storage_location, detected_format, detected_structure, uploaded_at_utc, "
            "uploaded_by) VALUES (?, ?, ?, ?, 'text/csv', 42, ?, ?, 'CSV', '{}', ?, ?)",
            (upload_id, engagement_id, kind, file_name, sha256, f"attachments/{engagement_id}/{sha256}.csv",
             STAMP, ACTOR),
        )
        return upload_id

    def add_import(self, period_id: str, upload_id: str | None = None, *, kind: str = "TB", import_no: int = 1,
                   status: str = "VALIDATED", active: int = 0, engagement_id: str | None = None,
                   **overrides: object) -> str:
        engagement_id = engagement_id or self.engagement_id
        upload_id = upload_id or self.add_upload(kind=kind,
                                                 file_name="ledger.csv" if kind == "GL" else "trial-balance.csv")
        import_id = new_id()
        columns: dict[str, object] = {
            "import_id": import_id,
            "engagement_id": engagement_id,
            "financial_period_id": period_id,
            "dataset_kind": kind,
            "import_no": import_no,
            "snapshot_label": f"{'Snapshot' if kind == 'GL' else 'Import'} #{import_no}",
            "status": status,
            "is_active": active,
            "source_upload_id": upload_id,
            "source_file_name": "trial-balance.csv",
            "source_file_sha256": digest("a"),
            "source_file_size_bytes": 42,
            "column_mapping_json": "{}",
            "fingerprint_hash": digest("b"),
            "imported_at_utc": STAMP,
            "imported_by": ACTOR,
        }
        columns.update(overrides)
        placeholders = ", ".join("?" for _ in columns)
        self.raw_sql(
            f"INSERT INTO dataset_import ({', '.join(columns)}) VALUES ({placeholders})",
            tuple(columns.values()),
        )
        return import_id

    def commit_import(self, import_id: str, *, active: int = 1) -> None:
        """Rows are written while an import is pending; the import is committed afterwards."""
        self.raw_sql(
            "UPDATE dataset_import SET status = 'IMPORTED', is_active = ? WHERE import_id = ?",
            (active, import_id),
        )

    def supersede_and_activate(self, previous_import_id: str, replacement_import_id: str) -> None:
        """The old version steps aside and the new one becomes active in one transaction."""
        self.raw_sql(
            "UPDATE dataset_import SET status = 'SUPERSEDED', is_active = 0, superseded_by_import_id = ? "
            "WHERE import_id = ?",
            (replacement_import_id, previous_import_id),
        )
        self.raw_sql("UPDATE dataset_import SET is_active = 1 WHERE import_id = ?", (replacement_import_id,))

    def add_tb_line(self, period_id: str, import_id: str, account_id: str, account_code: str, *,
                    line_no: int = 1, debit: int = 0, credit: int = 0, balance: int = 0,
                    row_hash: str | None = None, engagement_id: str | None = None) -> str:
        engagement_id = engagement_id or self.engagement_id
        line_id = new_id()
        self.raw_sql(
            "INSERT INTO tb_line (tb_line_id, import_id, engagement_id, financial_period_id, account_id, "
            "line_no, source_row_no, account_code, account_name, normalized_code, debit_minor, credit_minor, "
            "balance_minor, currency_code, extra_columns_json, row_hash, created_at_utc) "
            "VALUES (?, ?, ?, ?, ?, ?, 2, ?, ?, ?, ?, ?, ?, 'USD', '{}', ?, ?)",
            (line_id, import_id, engagement_id, period_id, account_id, line_no, account_code,
             f"Account {account_code}", account_code.upper(), debit, credit, balance,
             row_hash or digest("c"), STAMP),
        )
        return line_id

    def add_gl_journal(self, period_id: str, import_id: str, identity: str, *, line_count: int = 1,
                       engagement_id: str | None = None) -> str:
        engagement_id = engagement_id or self.engagement_id
        journal_id = new_id()
        self.raw_sql(
            "INSERT INTO gl_journal (gl_journal_id, import_id, engagement_id, financial_period_id, "
            "journal_identity, identity_source, journal_number, journal_source, posting_date, reference, "
            "description, currency_code, prepared_by, line_count, journal_hash, created_at_utc) "
            "VALUES (?, ?, ?, ?, ?, 'SOURCE', ?, 'GL', '2026-06-30', 'REF-1', 'Entry', 'USD', 'A. Clerk', ?, ?, ?)",
            (journal_id, import_id, engagement_id, period_id, identity, identity.split(":")[-1],
             line_count, digest("d"), STAMP),
        )
        return journal_id

    def add_gl_line(self, period_id: str, import_id: str, journal_id: str, account_code: str, *,
                    line_identity: str = "L1", transaction_date: str = "2026-06-30", debit: int = 0,
                    credit: int = 0, out_of_period: int = 0, account_id: str | None = None,
                    line_hash: str | None = None, value_hash: str | None = None, line_no: int = 1,
                    engagement_id: str | None = None) -> str:
        engagement_id = engagement_id or self.engagement_id
        line_id = new_id()
        self.raw_sql(
            "INSERT INTO gl_line (gl_line_id, gl_journal_id, import_id, engagement_id, financial_period_id, "
            "line_no, source_row_no, line_identity, identity_source, source_line_no, account_id, account_code, "
            "account_name, transaction_date, posting_date, description, debit_minor, credit_minor, amount_minor, "
            "currency_code, journal_source, reference, prepared_by, is_out_of_period, line_hash, value_hash, "
            "attribute_hash, extra_columns_json, created_at_utc) "
            "VALUES (?, ?, ?, ?, ?, ?, 7, ?, 'SOURCE', ?, ?, ?, ?, ?, '2026-06-30', 'Entry', ?, ?, ?, 'USD', "
            "'GL', 'REF-1', 'A. Clerk', ?, ?, ?, ?, '{}', ?)",
            (line_id, journal_id, import_id, engagement_id, period_id, line_no, line_identity, line_identity,
             account_id, account_code, f"Account {account_code}", transaction_date, debit, credit,
             debit - credit, out_of_period, line_hash or digest("e"), value_hash or digest("f"), digest("1"),
             STAMP),
        )
        return line_id

    def query(self, name: str, parameters: dict) -> list:
        return list(self.workspace.connection.execute(load_query(name), parameters))


class FinancialPeriodTests(FinancialDataTestCase):
    def test_period_backfill_is_idempotent_and_derived_from_the_financial_year(self) -> None:
        """A workspace created before 0007 receives a period for every engagement."""
        statement = backfill_statement()
        self.raw_sql(statement)

        periods = self.workspace.connection.execute(
            "SELECT * FROM financial_period WHERE engagement_id = ?", (self.engagement_id,)
        ).fetchall()
        self.assertEqual(len(periods), 1)
        period = periods[0]
        self.assertEqual(period["reporting_date"], "2026-12-31")
        self.assertEqual(period["status"], "OPEN")
        self.assertEqual(len(period["financial_period_id"]), 36)
        self.assertEqual(period["financial_period_id"], period["financial_period_id"].lower())

        # Running the backfill again must not create a second period.
        self.raw_sql(statement)
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) FROM financial_period WHERE engagement_id = ?", (self.engagement_id,)
            ).fetchone()[0],
            1,
        )

    def test_a_finalized_engagement_receives_a_locked_period(self) -> None:
        self.add_value(self.engagement_id, "4000", "Revenue", "850000000")
        self.workspace.finalize_engagement(self.engagement_id)

        self.raw_sql(backfill_statement())

        period = self.workspace.connection.execute(
            "SELECT * FROM financial_period WHERE engagement_id = ?", (self.engagement_id,)
        ).fetchone()
        self.assertEqual(period["status"], "LOCKED")

    def test_a_period_is_unique_per_engagement(self) -> None:
        self.add_period()
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_period()

    def test_a_locked_period_never_reopens(self) -> None:
        period_id = self.add_period()
        self.raw_sql("UPDATE financial_period SET status = 'LOCKED' WHERE financial_period_id = ?", (period_id,))
        for target in ("OPEN", "IN_PROGRESS", "FINALIZED"):
            with self.subTest(target=target):
                with self.assertRaises(sqlite3.IntegrityError) as caught:
                    self.raw_sql(
                        "UPDATE financial_period SET status = ? WHERE financial_period_id = ?", (target, period_id)
                    )
                self.assertIn("AWB-GUARD-PERIOD-TRANSITION", str(caught.exception))

    def test_an_open_period_may_be_finalized_then_locked(self) -> None:
        period_id = self.add_period()
        self.raw_sql("UPDATE financial_period SET status = 'IN_PROGRESS' WHERE financial_period_id = ?", (period_id,))
        self.raw_sql("UPDATE financial_period SET status = 'FINALIZED' WHERE financial_period_id = ?", (period_id,))
        self.raw_sql("UPDATE financial_period SET status = 'LOCKED' WHERE financial_period_id = ?", (period_id,))
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT status FROM financial_period WHERE financial_period_id = ?", (period_id,)
            ).fetchone()[0],
            "LOCKED",
        )

    def test_the_reporting_date_is_sealed_once_the_period_is_closed(self) -> None:
        period_id = self.add_period(status="FINALIZED")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE financial_period SET reporting_date = '2026-06-30' WHERE financial_period_id = ?",
                (period_id,),
            )
        self.assertIn("AWB-GUARD-PERIOD-SEALED", str(caught.exception))

    def test_a_period_is_never_deleted_or_moved_to_another_engagement(self) -> None:
        period_id = self.add_period()
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("DELETE FROM financial_period WHERE financial_period_id = ?", (period_id,))
        self.assertIn("AWB-GUARD-PERIOD-DELETE", str(caught.exception))

        other = self.create_year(self.company_id, "FY2027", 2027)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE financial_period SET engagement_id = ? WHERE financial_period_id = ?", (other, period_id)
            )
        self.assertIn("AWB-GUARD-PERIOD-IDENTITY", str(caught.exception))


class DatasetImportVersioningTests(FinancialDataTestCase):
    def setUp(self) -> None:
        super().setUp()
        self.period_id = self.add_period()
        self.upload_id = self.add_upload()

    def test_import_numbers_are_unique_per_engagement_and_dataset(self) -> None:
        self.add_import(self.period_id, self.upload_id, import_no=1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_import(self.period_id, self.upload_id, import_no=1)
        # The same number is fine for the other dataset kind and for another engagement.
        self.add_import(self.period_id, import_no=1, kind="GL")

    def test_only_one_active_version_per_dataset(self) -> None:
        self.add_import(self.period_id, self.upload_id, import_no=1, status="IMPORTED", active=1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_import(self.period_id, self.upload_id, import_no=2, status="IMPORTED", active=1)

    def test_a_superseded_version_must_name_a_later_replacement(self) -> None:
        first = self.add_import(self.period_id, self.upload_id, import_no=1, status="IMPORTED", active=1)
        second = self.add_import(self.period_id, self.upload_id, import_no=2, status="IMPORTED")

        # A version can only be superseded by a later version of the same dataset.
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE dataset_import SET status = 'SUPERSEDED', is_active = 0, superseded_by_import_id = ? "
                "WHERE import_id = ?",
                (first, second),
            )
        self.assertIn("AWB-GUARD-DATASET-SUPERSEDE", str(caught.exception))

        # The documented order works and keeps both versions addressable: the old
        # version steps aside and the new one becomes active in the same transaction.
        self.raw_sql(
            "UPDATE dataset_import SET status = 'SUPERSEDED', is_active = 0, superseded_by_import_id = ? "
            "WHERE import_id = ?",
            (second, first),
        )
        self.raw_sql("UPDATE dataset_import SET is_active = 1 WHERE import_id = ?", (second,))
        rows = self.workspace.connection.execute(
            "SELECT import_no, status, is_active, superseded_by_import_id FROM dataset_import "
            "WHERE engagement_id = ? ORDER BY import_no",
            (self.engagement_id,),
        ).fetchall()
        self.assertEqual([row["status"] for row in rows], ["SUPERSEDED", "IMPORTED"])
        self.assertEqual([row["is_active"] for row in rows], [0, 1])
        self.assertEqual(rows[0]["superseded_by_import_id"], second)
        self.assertIsNone(rows[1]["superseded_by_import_id"])

    def test_provenance_and_statistics_are_frozen(self) -> None:
        import_id = self.add_import(self.period_id, self.upload_id)

        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE dataset_import SET source_file_name = 'other.csv' WHERE import_id = ?", (import_id,)
            )
        self.assertIn("AWB-GUARD-DATASET-IDENTITY", str(caught.exception))

        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE dataset_import SET column_mapping_json = '{\"account_code\":1}' WHERE import_id = ?",
                (import_id,),
            )
        self.assertIn("AWB-GUARD-DATASET-IDENTITY", str(caught.exception))

        # Statistics are validated data; they may be written while the import is
        # pending and are frozen once rows exist.
        self.raw_sql(
            "UPDATE dataset_import SET status = 'VALIDATED', row_count = 2, total_debit_minor = 100 "
            "WHERE import_id = ?",
            (import_id,),
        )
        self.raw_sql("UPDATE dataset_import SET status = 'IMPORTED' WHERE import_id = ?", (import_id,))
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE dataset_import SET row_count = 3 WHERE import_id = ?", (import_id,))
        self.assertIn("AWB-GUARD-DATASET-STATS", str(caught.exception))

    def test_an_import_is_never_deleted(self) -> None:
        import_id = self.add_import(self.period_id, self.upload_id)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("DELETE FROM dataset_import WHERE import_id = ?", (import_id,))
        self.assertIn("AWB-GUARD-DATASET-DELETE", str(caught.exception))

    def test_the_source_upload_must_belong_to_the_same_engagement_and_dataset(self) -> None:
        foreign_engagement = self.create_year(self.company_id, "FY2027", 2027)
        foreign_upload = self.add_upload(foreign_engagement, kind="TB")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.add_import(self.period_id, foreign_upload)
        self.assertIn("AWB-GUARD-DATASET-UPLOAD", str(caught.exception))

        ledger_upload = self.add_upload(kind="GL")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.add_import(self.period_id, ledger_upload, kind="TB")
        self.assertIn("AWB-GUARD-DATASET-UPLOAD", str(caught.exception))

    def test_finalization_metadata_is_retained_when_a_version_is_superseded(self) -> None:
        first = self.add_import(
            self.period_id, self.upload_id, import_no=1, status="FINALIZED",
            finalized_at_utc=STAMP, finalized_by=ACTOR,
        )
        second = self.add_import(self.period_id, self.upload_id, import_no=2, status="IMPORTED")
        self.raw_sql(
            "UPDATE dataset_import SET status = 'SUPERSEDED', is_active = 0, superseded_by_import_id = ? "
            "WHERE import_id = ?",
            (second, first),
        )
        row = self.workspace.connection.execute(
            "SELECT status, finalized_at_utc, finalized_by FROM dataset_import WHERE import_id = ?", (first,)
        ).fetchone()
        self.assertEqual(row["status"], "SUPERSEDED")
        self.assertEqual(row["finalized_at_utc"], STAMP)
        self.assertEqual(row["finalized_by"], ACTOR)

    def test_a_finalized_version_must_record_who_finalized_it(self) -> None:
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_import(self.period_id, self.upload_id, status="FINALIZED")


class ImportedLineTests(FinancialDataTestCase):
    def setUp(self) -> None:
        super().setUp()
        self.period_id = self.add_period()
        self.upload_id = self.add_upload()
        self.account_id = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="1000", account_name="Cash"
        )

    def test_lines_can_only_be_written_to_a_pending_import(self) -> None:
        committed = self.add_import(self.period_id, self.upload_id, status="IMPORTED")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.add_tb_line(self.period_id, committed, self.account_id, "1000")
        self.assertIn("AWB-GUARD-DATASET-LOCKED", str(caught.exception))

        # A ledger snapshot accepts lines while it is pending and refuses them once committed.
        gl_pending = self.add_import(self.period_id, kind="GL")
        journal_id = self.add_gl_journal(self.period_id, gl_pending, "SRC:J1", line_count=2)
        self.add_gl_line(self.period_id, gl_pending, journal_id, "1000", line_identity="L1")
        self.commit_import(gl_pending)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.add_gl_line(self.period_id, gl_pending, journal_id, "1000", line_identity="L2", line_no=2)
        self.assertIn("AWB-GUARD-DATASET-LOCKED", str(caught.exception))

    def test_a_closed_period_refuses_new_imported_rows(self) -> None:
        pending = self.add_import(self.period_id, self.upload_id)  # DRAFT, so only the period guard applies
        self.raw_sql("UPDATE financial_period SET status = 'FINALIZED' WHERE financial_period_id = ?",
                     (self.period_id,))
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.add_tb_line(self.period_id, pending, self.account_id, "1000")
        self.assertIn("AWB-GUARD-PERIOD-LOCKED", str(caught.exception))

    def test_imported_lines_are_append_only(self) -> None:
        pending = self.add_import(self.period_id, self.upload_id)
        line_id = self.add_tb_line(self.period_id, pending, self.account_id, "1000", debit=100, balance=100)
        for sql in (
            "UPDATE tb_line SET debit_minor = 999 WHERE tb_line_id = ?",
            "DELETE FROM tb_line WHERE tb_line_id = ?",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(sqlite3.IntegrityError) as caught:
                    self.raw_sql(sql, (line_id,))
                self.assertIn("AWB-GUARD-TB-APPEND-ONLY", str(caught.exception))

        gl_pending = self.add_import(self.period_id, kind="GL")
        journal_id = self.add_gl_journal(self.period_id, gl_pending, "SRC:J1")
        gl_line_id = self.add_gl_line(self.period_id, gl_pending, journal_id, "1000", debit=100)
        for sql in (
            "UPDATE gl_line SET debit_minor = 999 WHERE gl_line_id = ?",
            "DELETE FROM gl_line WHERE gl_line_id = ?",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(sqlite3.IntegrityError) as caught:
                    self.raw_sql(sql, (gl_line_id,))
                self.assertIn("AWB-GUARD-GL-APPEND-ONLY", str(caught.exception))

    def test_a_trial_balance_import_carries_each_account_once(self) -> None:
        pending = self.add_import(self.period_id, self.upload_id)
        self.add_tb_line(self.period_id, pending, self.account_id, "1000", line_no=1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_tb_line(self.period_id, pending, self.account_id, "1000", line_no=2)

    def test_a_trial_balance_line_cannot_reference_another_engagements_account(self) -> None:
        other_engagement = self.create_year(self.company_id, "FY2027", 2027)
        other_account = self.workspace.add_account(
            engagement_id=other_engagement, account_code="1000", account_name="Cash"
        )
        pending = self.add_import(self.period_id, self.upload_id)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_tb_line(self.period_id, pending, other_account, "1000")

    def test_a_ledger_line_may_keep_an_unknown_account_unlinked(self) -> None:
        pending = self.add_import(self.period_id, kind="GL")
        journal_id = self.add_gl_journal(self.period_id, pending, "SRC:J1")
        line_id = self.add_gl_line(self.period_id, pending, journal_id, "9999", account_id=None, debit=500)
        row = self.workspace.connection.execute(
            "SELECT account_id, account_code, amount_minor FROM gl_line WHERE gl_line_id = ?", (line_id,)
        ).fetchone()
        self.assertIsNone(row["account_id"])
        self.assertEqual(row["account_code"], "9999")
        self.assertEqual(row["amount_minor"], 500)

        foreign_account = self.workspace.add_account(
            engagement_id=self.create_year(self.company_id, "FY2028", 2028), account_code="1000",
            account_name="Cash",
        )
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_gl_line(self.period_id, pending, journal_id, "1000", line_identity="L2",
                             account_id=foreign_account)

    def test_out_of_period_transactions_are_flagged_not_dropped(self) -> None:
        pending = self.add_import(self.period_id, kind="GL")
        journal_id = self.add_gl_journal(self.period_id, pending, "SRC:J1", line_count=2)
        self.add_gl_line(self.period_id, pending, journal_id, "1000", line_identity="L1",
                         transaction_date="2027-01-15", debit=100, out_of_period=1)
        self.add_gl_line(self.period_id, pending, journal_id, "1000", line_identity="L2",
                         transaction_date="2026-06-30", debit=100, line_no=2)

        rows = self.query("gl_out_of_period.sql",
                          {"import_id": pending, "page_size": 50, "offset": 0})
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["transaction_date"], "2027-01-15")
        self.assertEqual(rows[0]["source_row_no"], 7)
        self.assertEqual(rows[0]["total_count"], 1)

    def test_a_transaction_date_outside_the_calendar_is_refused(self) -> None:
        pending = self.add_import(self.period_id, kind="GL")
        journal_id = self.add_gl_journal(self.period_id, pending, "SRC:J1")
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_gl_line(self.period_id, pending, journal_id, "1000", transaction_date="2026-13-45")


class ImportJobTests(FinancialDataTestCase):
    def setUp(self) -> None:
        super().setUp()
        self.period_id = self.add_period()
        self.upload_id = self.add_upload()

    def add_job(self, status: str = "QUEUED") -> str:
        job_id = new_id()
        self.raw_sql(
            "INSERT INTO import_job (job_id, engagement_id, financial_period_id, dataset_kind, status, stage, "
            "upload_id, column_mapping_json, attempt_no, allow_unbalanced, allow_repeat, cancel_requested, "
            "processed_rows, total_rows, message, requested_at_utc, requested_by) "
            "VALUES (?, ?, ?, 'TB', ?, ?, ?, '{}', 1, 0, 0, 0, 0, 0, '', ?, ?)",
            (job_id, self.engagement_id, self.period_id, status, status, self.upload_id, STAMP, ACTOR),
        )
        return job_id

    def test_an_import_job_walks_the_observable_stages(self) -> None:
        job_id = self.add_job()
        for status in ("PROCESSING", "VALIDATING", "IMPORTING", "RECONCILING", "COMPLETED"):
            self.raw_sql(
                "UPDATE import_job SET status = ?, stage = ?, processed_rows = processed_rows + 10, "
                "total_rows = 100 WHERE job_id = ?",
                (status, status, job_id),
            )
        row = self.workspace.connection.execute(
            "SELECT status, processed_rows FROM import_job WHERE job_id = ?", (job_id,)
        ).fetchone()
        self.assertEqual(row["status"], "COMPLETED")
        self.assertEqual(row["processed_rows"], 50)

    def test_an_import_job_cannot_skip_stages(self) -> None:
        job_id = self.add_job()
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE import_job SET status = 'COMPLETED' WHERE job_id = ?", (job_id,))
        self.assertIn("AWB-GUARD-JOB-TRANSITION", str(caught.exception))

    def test_import_job_history_is_retained(self) -> None:
        job_id = self.add_job()
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("DELETE FROM import_job WHERE job_id = ?", (job_id,))
        self.assertIn("AWB-GUARD-JOB-DELETE", str(caught.exception))

        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE import_job SET attempt_no = 2 WHERE job_id = ?", (job_id,))
        self.assertIn("AWB-GUARD-JOB-IDENTITY", str(caught.exception))


class MaterialityAndAuditAreaTests(FinancialDataTestCase):
    def setUp(self) -> None:
        super().setUp()
        self.period_id = self.add_period()

    def add_materiality(self, version_no: int = 1, supersedes_id: str | None = None, *,
                        overall: int = 10_000_000, status: str = "DRAFT") -> str:
        materiality_id = new_id()
        approved_at = STAMP if status == "APPROVED" else None
        approved_by = ACTOR if status == "APPROVED" else None
        self.raw_sql(
            "INSERT INTO engagement_materiality (materiality_id, engagement_id, financial_period_id, version_no, "
            "status, overall_materiality_minor, performance_materiality_minor, clearly_trivial_threshold_minor, "
            "currency_code, basis_note, determined_at_utc, determined_by, approved_at_utc, approved_by, "
            "supersedes_id, row_version) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'USD', '5% of revenue', ?, ?, ?, ?, ?, 1)",
            (materiality_id, self.engagement_id, self.period_id, version_no, status, overall, overall // 2,
             overall // 10, STAMP, ACTOR, approved_at, approved_by, supersedes_id),
        )
        return materiality_id

    def test_materiality_amounts_are_immutable(self) -> None:
        materiality_id = self.add_materiality()
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "UPDATE engagement_materiality SET overall_materiality_minor = 99 WHERE materiality_id = ?",
                (materiality_id,),
            )
        self.assertIn("AWB-GUARD-MATERIALITY-IMMUTABLE", str(caught.exception))

    def test_materiality_versions_must_chain(self) -> None:
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_materiality(version_no=2)  # a later version must name the version it supersedes

    def test_materiality_approval_requires_the_approver(self) -> None:
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql(
                "INSERT INTO engagement_materiality (materiality_id, engagement_id, financial_period_id, version_no, "
                "status, overall_materiality_minor, currency_code, basis_note, determined_at_utc, determined_by, "
                "row_version) VALUES (?, ?, ?, 1, 'APPROVED', 1000, 'USD', '', ?, ?, 1)",
                (new_id(), self.engagement_id, self.period_id, STAMP, ACTOR),
            )

    def test_audit_areas_are_unique_per_engagement_and_optional(self) -> None:
        area_id = new_id()
        self.raw_sql(
            "INSERT INTO audit_area (audit_area_id, engagement_id, area_code, area_name, display_order, "
            "created_at_utc, created_by, row_version) VALUES (?, ?, 'REVENUE', 'Revenue', 1, ?, ?, 1)",
            (area_id, self.engagement_id, STAMP, ACTOR),
        )
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql(
                "INSERT INTO audit_area (audit_area_id, engagement_id, area_code, area_name, display_order, "
                "created_at_utc, created_by, row_version) VALUES (?, ?, 'REVENUE', 'Revenue again', 2, ?, ?, 1)",
                (new_id(), self.engagement_id, STAMP, ACTOR),
            )

        # The same code is fine for another engagement.
        other = self.create_year(self.company_id, "FY2027", 2027)
        self.raw_sql(
            "INSERT INTO audit_area (audit_area_id, engagement_id, area_code, area_name, display_order, "
            "created_at_utc, created_by, row_version) VALUES (?, ?, 'REVENUE', 'Revenue', 1, ?, ?, 1)",
            (new_id(), other, STAMP, ACTOR),
        )

    def test_an_account_may_only_be_linked_to_an_area_of_its_own_engagement(self) -> None:
        area_id = new_id()
        self.raw_sql(
            "INSERT INTO audit_area (audit_area_id, engagement_id, area_code, area_name, display_order, "
            "created_at_utc, created_by, row_version) VALUES (?, ?, 'REVENUE', 'Revenue', 1, ?, ?, 1)",
            (area_id, self.engagement_id, STAMP, ACTOR),
        )
        account_id = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="4000", account_name="Revenue"
        )
        self.raw_sql(
            "UPDATE account SET audit_area_id = ?, normalized_code = '4000' WHERE account_id = ?",
            (area_id, account_id),
        )

        other_engagement = self.create_year(self.company_id, "FY2027", 2027)
        other_account = self.workspace.add_account(
            engagement_id=other_engagement, account_code="4000", account_name="Revenue"
        )
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE account SET audit_area_id = ? WHERE account_id = ?", (area_id, other_account))
        self.assertIn("AWB-GUARD-ACCOUNT-AREA", str(caught.exception))


class AccountMasterTests(FinancialDataTestCase):
    def test_account_origin_is_provenance_and_cannot_be_rewritten(self) -> None:
        account_id = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="1000", account_name="Cash"
        )
        row = self.workspace.connection.execute(
            "SELECT normalized_code, account_origin FROM account WHERE account_id = ?", (account_id,)
        ).fetchone()
        self.assertEqual(row["normalized_code"], "1000")  # derived comparison key
        self.assertEqual(row["account_origin"], "MANUAL")

        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE account SET account_origin = 'TB' WHERE account_id = ?", (account_id,))
        self.assertIn("AWB-GUARD-ACCOUNT-ORIGIN", str(caught.exception))

        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE account SET account_origin = 'WHATEVER' WHERE account_id = ?", (account_id,))
        self.assertIn("AWB-GUARD-ACCOUNT-ORIGIN", str(caught.exception))

    def test_account_codes_are_unique_per_engagement_only(self) -> None:
        self.workspace.add_account(engagement_id=self.engagement_id, account_code="400100", account_name="Revenue")
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql(
                "INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, "
                "display_order, created_at_utc, created_by, normalized_code, account_origin) "
                "VALUES (?, ?, '400100', 'Revenue branch', 'INCOME', 20, ?, ?, '400100', 'TB')",
                (new_id(), self.engagement_id, STAMP, ACTOR),
            )

        # A second client may use the same code: uniqueness is engagement-scoped.
        other = self.create_year(self.company_id, "FY2027", 2027)
        self.workspace.add_account(engagement_id=other, account_code="400100", account_name="Revenue")


class FinancialQueryTests(FinancialDataTestCase):
    """The db/sql read queries execute against the imported rows and never hide a difference."""

    def setUp(self) -> None:
        super().setUp()
        self.period_id = self.add_period()
        self.tb_upload = self.add_upload(file_name="tb-2026.csv")
        self.gl_upload = self.add_upload(kind="GL", file_name="gl-2026.csv", sha256=digest("9"))
        self.cash = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="1000", account_name="Cash"
        )
        self.revenue = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="4000", account_name="Revenue"
        )

        self.tb_import = self.add_import(self.period_id, self.tb_upload, import_no=1)
        self.add_tb_line(self.period_id, self.tb_import, self.cash, "1000", line_no=1, debit=50000, balance=50000,
                         row_hash=digest("1"))
        self.add_tb_line(self.period_id, self.tb_import, self.revenue, "4000", line_no=2, credit=50000,
                         balance=-50000, row_hash=digest("2"))
        self.commit_import(self.tb_import)

        self.gl_import = self.add_import(self.period_id, self.gl_upload, kind="GL", import_no=1)
        journal_id = self.add_gl_journal(self.period_id, self.gl_import, "SRC:J1", line_count=3)
        self.add_gl_line(self.period_id, self.gl_import, journal_id, "1000", line_identity="L1", debit=50000)
        self.add_gl_line(self.period_id, self.gl_import, journal_id, "4000", line_identity="L2", credit=50000,
                         line_no=2, account_id=self.revenue)
        self.add_gl_line(self.period_id, self.gl_import, journal_id, "9999", line_identity="L3", debit=1000,
                         line_no=3, account_id=None)
        self.commit_import(self.gl_import)

    def test_trial_balance_search_is_paged_and_filtered_in_sql(self) -> None:
        rows = self.query(
            "tb_search.sql",
            {
                "import_id": self.tb_import,
                "account_code": "%4000%",
                "account_name": None,
                "account_group": None,
                "min_balance": None,
                "max_balance": None,
                "page_size": 10,
                "offset": 0,
            },
        )
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["account_code"], "4000")
        self.assertEqual(rows[0]["total_count"], 1)

        everything = self.query(
            "tb_search.sql",
            {
                "import_id": self.tb_import,
                "account_code": None,
                "account_name": None,
                "account_group": None,
                "min_balance": None,
                "max_balance": None,
                "page_size": 1,
                "offset": 0,
            },
        )
        self.assertEqual(len(everything), 1)
        self.assertEqual(everything[0]["total_count"], 2)

    def test_ledger_search_filters_by_identity_date_and_amount(self) -> None:
        rows = self.query(
            "gl_search.sql",
            {
                "import_id": self.gl_import,
                "transaction_id": "%J1%",
                "account_code": None,
                "date_from": "2026-01-01",
                "date_to": "2026-12-31",
                "min_amount": None,
                "max_amount": None,
                "description": None,
                "journal_source": None,
                "reference": None,
                "out_of_period_only": 0,
                "page_size": 50,
                "offset": 0,
            },
        )
        self.assertEqual(len(rows), 3)
        self.assertEqual(rows[0]["journal_identity"], "SRC:J1")
        self.assertEqual(rows[0]["source_row_no"], 7)

        out_of_period = self.query(
            "gl_search.sql",
            {
                "import_id": self.gl_import,
                "transaction_id": None,
                "account_code": None,
                "date_from": None,
                "date_to": None,
                "min_amount": 100,
                "max_amount": 1000,
                "description": None,
                "journal_source": None,
                "reference": None,
                "out_of_period_only": 0,
                "page_size": 50,
                "offset": 0,
            },
        )
        self.assertEqual([row["account_code"] for row in out_of_period], ["9999"])

    def test_reconciliation_uses_the_debit_positive_sign_convention(self) -> None:
        rows = self.query(
            "tb_gl_reconciliation.sql",
            {"tb_import_id": self.tb_import, "gl_import_id": self.gl_import},
        )
        by_code = {row["account_code"]: row for row in rows}

        self.assertEqual(by_code["1000"]["tb_balance_minor"], 50000)
        self.assertEqual(by_code["1000"]["gl_net_minor"], 50000)
        self.assertEqual(by_code["1000"]["gl_line_count"], 1)

        # A credit balance stays negative on both sides: no sign normalization hides a difference.
        self.assertEqual(by_code["4000"]["tb_balance_minor"], -50000)
        self.assertEqual(by_code["4000"]["gl_net_minor"], -50000)

        # An account only in the ledger is returned with NULLs on the TB side.
        self.assertIsNone(by_code["9999"]["tb_balance_minor"])
        self.assertEqual(by_code["9999"]["gl_net_minor"], 1000)

    def test_reconciliation_reports_a_trial_balance_account_without_ledger_activity(self) -> None:
        inventory = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="1300", account_name="Inventory"
        )
        extra = self.add_import(self.period_id, self.add_upload(sha256=digest("3"), file_name="tb-extra.csv"),
                                import_no=9)
        self.add_tb_line(self.period_id, extra, inventory, "1300", line_no=1, debit=7500, balance=7500,
                         row_hash=digest("5"))
        self.commit_import(extra, active=0)

        rows = self.query(
            "tb_gl_reconciliation.sql",
            {"tb_import_id": extra, "gl_import_id": self.gl_import},
        )
        row = next(candidate for candidate in rows if candidate["account_code"] == "1300")
        self.assertEqual(row["tb_balance_minor"], 7500)
        self.assertIsNone(row["gl_net_minor"])
        self.assertEqual(row["gl_line_count"], 0)

    def test_trial_balance_versions_are_compared_account_by_account(self) -> None:
        second_upload = self.add_upload(sha256=digest("8"), file_name="tb-2026-v2.csv")
        second = self.add_import(self.period_id, second_upload, import_no=2)
        self.add_tb_line(self.period_id, second, self.cash, "1000", line_no=1, debit=60000, balance=60000,
                         row_hash=digest("3"))
        self.add_tb_line(self.period_id, second, self.revenue, "4000", line_no=2, credit=50000, balance=-50000,
                         row_hash=digest("2"))
        accrued = self.workspace.add_account(
            engagement_id=self.engagement_id, account_code="2100", account_name="Accruals"
        )
        self.add_tb_line(self.period_id, second, accrued, "2100", line_no=3, credit=10000, balance=-10000,
                         row_hash=digest("4"))
        self.commit_import(second, active=0)
        self.supersede_and_activate(self.tb_import, second)

        rows = self.query(
            "tb_version_comparison.sql",
            {"previous_import_id": self.tb_import, "current_import_id": second},
        )
        states = {row["account_code"]: row["state"] for row in rows}
        self.assertEqual(states["1000"], "CHANGED_VALUE")
        self.assertEqual(states["4000"], "UNCHANGED")
        self.assertEqual(states["2100"], "ADDED")

    def test_a_later_ledger_snapshot_distinguishes_every_roll_forward_state(self) -> None:
        second_upload = self.add_upload(kind="GL", sha256=digest("7"), file_name="gl-2027.csv")
        second = self.add_import(self.period_id, second_upload, kind="GL", import_no=2,
                                 coverage_through_date="2027-03-31")
        journal_id = self.add_gl_journal(self.period_id, second, "SRC:J1", line_count=3)

        # L1 unchanged, L2 changed amount, L3 added; the earlier L4 transaction is gone.
        self.add_gl_line(self.period_id, second, journal_id, "1000", line_identity="L1", debit=5000,
                         line_hash=digest("1"), value_hash=digest("2"))
        self.add_gl_line(self.period_id, second, journal_id, "1000", line_identity="L2", debit=9999,
                         line_no=2, line_hash=digest("3"), value_hash=digest("4"))
        self.add_gl_line(self.period_id, second, journal_id, "4000", line_identity="L3", credit=2500,
                         line_no=3, account_id=self.revenue, line_hash=digest("5"), value_hash=digest("6"))
        # Two snapshots coexist without overwriting each other; the comparison takes
        # the import ids explicitly, exactly as the roll-forward view will.
        self.commit_import(second, active=0)

        previous_upload = self.add_upload(kind="GL", sha256=digest("6"), file_name="gl-2026b.csv")
        previous = self.add_import(self.period_id, previous_upload, kind="GL", import_no=3)
        previous_journal = self.add_gl_journal(self.period_id, previous, "SRC:J1", line_count=3)
        self.add_gl_line(self.period_id, previous, previous_journal, "1000", line_identity="L1", debit=5000,
                         line_hash=digest("1"), value_hash=digest("2"))
        self.add_gl_line(self.period_id, previous, previous_journal, "1000", line_identity="L2", debit=4000,
                         line_no=2, line_hash=digest("a"), value_hash=digest("b"))
        self.add_gl_line(self.period_id, previous, previous_journal, "1000", line_identity="L4", debit=700,
                         line_no=3, line_hash=digest("c"), value_hash=digest("d"))
        self.commit_import(previous, active=0)

        summary = self.query(
            "gl_snapshot_comparison_summary.sql",
            {"previous_import_id": previous, "current_import_id": second},
        )[0]
        self.assertEqual(summary["unchanged_count"], 1)
        self.assertEqual(summary["changed_value_count"], 1)
        self.assertEqual(summary["changed_attributes_count"], 0)
        self.assertEqual(summary["added_count"], 1)
        self.assertEqual(summary["removed_count"], 1)
        self.assertEqual(summary["added_debit_minor"], 0)
        self.assertEqual(summary["added_credit_minor"], 2500)
        self.assertEqual(summary["removed_debit_minor"], 700)
        self.assertEqual(summary["changed_value_delta_minor"], 5999)

        detail = self.query(
            "gl_snapshot_comparison_detail.sql",
            {
                "previous_import_id": previous,
                "current_import_id": second,
                "state": "ALL",
                "page_size": 50,
                "offset": 0,
            },
        )
        states = {row["line_identity"]: row["state"] for row in detail}
        self.assertEqual(states["L1"], "UNCHANGED")
        self.assertEqual(states["L2"], "CHANGED_VALUE")
        self.assertEqual(states["L3"], "ADDED")
        self.assertEqual(states["L4"], "REMOVED")
        self.assertEqual(len(detail), detail[0]["total_count"])

    def test_a_recategorised_account_is_reported_as_a_changed_attribute(self) -> None:
        second_upload = self.add_upload(kind="GL", sha256=digest("5"), file_name="gl-2026-v2.csv")
        second = self.add_import(self.period_id, second_upload, kind="GL", import_no=4)
        journal_id = self.add_gl_journal(self.period_id, second, "SRC:J1", line_count=1)
        # Same identity, same money, different account: a changed attribute, not a changed amount.
        self.add_gl_line(self.period_id, second, journal_id, "4000", line_identity="L1", debit=50000,
                         account_id=self.revenue, line_hash=digest("8"), value_hash=digest("f"))
        self.commit_import(second, active=0)

        previous_upload = self.add_upload(kind="GL", sha256=digest("4"), file_name="gl-2026-v1.csv")
        previous = self.add_import(self.period_id, previous_upload, kind="GL", import_no=5)
        previous_journal = self.add_gl_journal(self.period_id, previous, "SRC:J1", line_count=1)
        self.add_gl_line(self.period_id, previous, previous_journal, "1000", line_identity="L1", debit=50000,
                         line_hash=digest("7"), value_hash=digest("f"))
        self.commit_import(previous, active=0)

        summary = self.query(
            "gl_snapshot_comparison_summary.sql",
            {"previous_import_id": previous, "current_import_id": second},
        )[0]
        self.assertEqual(summary["changed_attributes_count"], 1)
        self.assertEqual(summary["changed_value_count"], 0)


if __name__ == "__main__":
    unittest.main()
