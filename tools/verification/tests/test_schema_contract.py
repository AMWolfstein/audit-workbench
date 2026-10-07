"""Schema contract: migrations, constraints and the manifest digest specification."""

from __future__ import annotations

import sqlite3
import unittest
from pathlib import Path

from _harness import REPO_ROOT, Workspace, WorkspaceError, WorkspaceTestCase
from workspace import build_manifest_document, sha256_hex

FIXTURE_DIR = REPO_ROOT / "tests" / "fixtures"


class SchemaContractTests(WorkspaceTestCase):
    def test_migrations_are_recorded_with_checksums(self) -> None:
        rows = list(self.workspace.connection.execute("SELECT * FROM schema_migration ORDER BY migration_id"))
        self.assertEqual([row["migration_id"] for row in rows], ["0001_initial_schema", "0002_integrity_guards", "0003_team_foundation", "0004_engagement_currency_guard", "0005_client_handover", "0006_foundation_integrity"])
        for row in rows:
            self.assertEqual(len(row["checksum_sha256"]), 64)

    def test_reopening_a_workspace_is_idempotent(self) -> None:
        self.workspace.close()
        reopened = Workspace.create(self.temp_dir / "workspace.db")
        try:
            self.assertEqual(reopened.apply_migrations(), [])
        finally:
            reopened.close()

    def test_changed_migration_is_refused(self) -> None:
        self.workspace.connection.execute(
            "UPDATE schema_migration SET checksum_sha256 = ? WHERE migration_id = '0001_initial_schema'",
            ("0" * 64,),
        )
        with self.assertRaises(WorkspaceError) as caught:
            self.workspace.apply_migrations()
        self.assertIn("changed after it was applied", str(caught.exception))

    def test_foreign_keys_are_enforced(self) -> None:
        self.assertEqual(self.workspace.connection.execute("PRAGMA foreign_keys").fetchone()[0], 1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.workspace.connection.execute(
                "INSERT INTO engagement (engagement_id, company_id, financial_year_id, status, currency_code, "
                "minor_unit_scale, created_at_utc, created_by) VALUES ('e1', 'missing-company', 'missing-year', "
                "'DRAFT', 'USD', 2, '2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')"
            )

    def test_check_constraints_reject_invalid_enumerations(self) -> None:
        company_id = self.create_company()
        year_id = self.workspace.connection.execute("SELECT financial_year_id FROM financial_year").fetchone()
        self.assertIsNone(year_id)  # no engagement created yet, so no financial year exists

        fy = self.create_year(company_id, "FY2026", 2026)
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql("UPDATE engagement SET status = 'WHATEVER' WHERE engagement_id = ?", (fy,))

    def test_strict_tables_reject_wrong_types(self) -> None:
        company_id = self.create_company()
        fy = self.create_year(company_id, "FY2026", 2026)
        account_id = self.workspace.add_account(
            engagement_id=fy, account_code="4000", account_name="Revenue"
        )
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql(
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, recorded_at_utc, recorded_by) VALUES ('x', ?, ?, 1, "
                "'not-a-number', 'USD', '2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
                (fy, account_id),
            )

    def test_unique_constraints(self) -> None:
        company_id = self.create_company()
        fy = self.create_year(company_id, "FY2026", 2026)
        self.workspace.add_account(engagement_id=fy, account_code="4000", account_name="Revenue")
        with self.assertRaises(sqlite3.IntegrityError):
            self.raw_sql(
                "INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, "
                "display_order, created_at_utc, created_by) VALUES ('dup', ?, '4000', 'Duplicate', 'INCOME', 1, "
                "'2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
                (fy,),
            )

    def test_companies_are_archived_not_deleted(self) -> None:
        company_id = self.create_company()
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("DELETE FROM company WHERE company_id = ?", (company_id,))
        self.assertIn("AWB-GUARD-COMPANY-DELETE", str(caught.exception))

    def test_financial_year_in_use_cannot_be_re_dated(self) -> None:
        company_id = self.create_company()
        self.create_year(company_id, "FY2026", 2026)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE financial_year SET period_end = '2026-06-30'")
        self.assertIn("AWB-GUARD-FINANCIAL-YEAR-IN-USE", str(caught.exception))

    def test_revision_chain_is_enforced(self) -> None:
        company_id = self.create_company()
        fy = self.create_year(company_id, "FY2026", 2026)
        account_id, _ = self.add_value(fy, "4000", "Revenue", "100")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, supersedes_id, recorded_at_utc, recorded_by) VALUES ('skip', ?, ?, 5, "
                "1, 'USD', NULL, '2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
                (fy, account_id),
            )
        self.assertIn("ck_financial_data_first_revision", str(caught.exception))

    def test_value_currency_must_match_engagement(self) -> None:
        company_id = self.create_company()
        fy = self.create_year(company_id, "FY2026", 2026)
        account_id = self.workspace.add_account(engagement_id=fy, account_code="4000", account_name="Revenue")
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, recorded_at_utc, recorded_by) VALUES ('cur', ?, ?, 1, 1, 'EUR', "
                "'2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
                (fy, account_id),
            )
        self.assertIn("AWB-GUARD-FINANCIAL-DATA-CURRENCY", str(caught.exception))

    def test_external_principal_cannot_be_reactivated_or_given_access(self) -> None:
        user_id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        self.raw_sql(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) "
            "VALUES (?,'external.user','External User','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            (user_id,),
        )
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE app_user SET status='ACTIVE' WHERE user_id=?", (user_id,))
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))

        company_id = self.create_company()
        engagement_id = self.create_year(company_id, "FY2026", 2026)
        role_id = self.workspace.connection.execute(
            "SELECT role_id FROM app_role WHERE role_key='PARTNER'"
        ).fetchone()[0]
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO engagement_member VALUES ('membership',?,?,?,?,?,?,?,1)",
                (engagement_id, user_id, role_id, "ACTIVE", "2026-01-01T00:00:00.000Z",
                 "00000000-0000-4000-8000-000000000001", "2026-01-01T00:00:00.000Z"),
            )
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))

    def test_client_import_evidence_is_append_only(self) -> None:
        self.raw_sql(
            "INSERT INTO client_import VALUES ('import','package',?,'company','2026-01-01T00:00:00.000Z',?, '{}','{}')",
            ("a" * 64, "00000000-0000-4000-8000-000000000001"),
        )
        for sql in (
            "UPDATE client_import SET team_history_json='[]'",
            "DELETE FROM client_import",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(sqlite3.IntegrityError) as caught:
                    self.raw_sql(sql)
                self.assertIn("AWB-GUARD-IMPORT-APPEND-ONLY", str(caught.exception))

    def test_imported_audit_history_is_append_only(self) -> None:
        self.raw_sql(
            "INSERT INTO client_import VALUES ('import','package',?,'company','2026-01-01T00:00:00.000Z',?, '{}','{}')",
            ("a" * 64, "00000000-0000-4000-8000-000000000001"),
        )
        self.raw_sql(
            "INSERT INTO imported_audit_event VALUES ('event','import','source',1,"
            "'2026-01-01T00:00:00.000Z',?,'External User','COMPANY_CREATED','SUCCESS','company',NULL,"
            "'COMPANY','company','Imported history.','{}',NULL,?)",
            ("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "b" * 64),
        )
        for sql in (
            "UPDATE imported_audit_event SET description='rewritten'",
            "DELETE FROM imported_audit_event",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(sqlite3.IntegrityError) as caught:
                    self.raw_sql(sql)
                self.assertIn("AWB-GUARD-IMPORTED-AUDIT-APPEND-ONLY", str(caught.exception))

    def test_external_principal_must_be_created_disabled_and_non_local(self) -> None:
        # The only legitimate shape is a disabled, non-local attribution-only identity
        # (exactly what ClientHandoverPackageService imports); every other shape aborts.
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) "
                "VALUES (?,'ext.active','External Active','ACTIVE',0,'2026-01-01T00:00:00.000Z',1)",
                ("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",),
            )
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) "
                "VALUES (?,'ext.local','External Local','DISABLED',1,'2026-01-01T00:00:00.000Z',1)",
                ("cccccccc-cccc-4ccc-8ccc-cccccccccccc",),
            )
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))
        self.raw_sql(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) "
            "VALUES (?,'ext.imported','External Imported','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            ("dddddddd-dddd-4ddd-8ddd-dddddddddddd",),
        )
        row = self.workspace.connection.execute(
            "SELECT status, is_local_demo, is_external_principal FROM app_user WHERE username='ext.imported'"
        ).fetchone()
        self.assertEqual((row["status"], row["is_local_demo"], row["is_external_principal"]), ("DISABLED", 0, 1))

    def test_external_principal_cannot_shed_its_marker_or_receive_assignments(self) -> None:
        user_id = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"
        self.raw_sql(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) "
            "VALUES (?,'ext.assigned','External Assigned','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            (user_id,),
        )
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql("UPDATE app_user SET is_external_principal=0 WHERE user_id=?", (user_id,))
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))

        company_id = self.create_company()
        engagement_id = self.create_year(company_id, "FY2026", 2026)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            self.raw_sql(
                "INSERT INTO assignment VALUES ('assignment',?,?,?,?,?,?,?,?,?,1)",
                (engagement_id, user_id, "ENGAGEMENT", "ALL", "Imported follow-up", "ACTIVE",
                 "2026-01-01T00:00:00.000Z", "00000000-0000-4000-8000-000000000001",
                 "2026-01-01T00:00:00.000Z"),
            )
        self.assertIn("AWB-GUARD-EXTERNAL-PRINCIPAL", str(caught.exception))

    def test_financial_year_definitions_may_be_duplicated_under_different_ids(self) -> None:
        """D-1 (ADR-027): ux_financial_year_definition was dropped because financial-year
        ids are embedded in finalized manifests and must survive a handover unchanged."""
        indexes = {
            row["name"]
            for row in self.workspace.connection.execute("SELECT name FROM sqlite_master WHERE type='index'")
        }
        self.assertNotIn("ux_financial_year_definition", indexes)

        for year_id in ("11111111-2222-4333-8333-000000000001", "11111111-2222-4333-8333-000000000002"):
            self.raw_sql(
                "INSERT INTO financial_year VALUES (?,'SHARED-FY','2026-01-01','2026-12-31','2026-01-01T00:00:00.000Z')",
                (year_id,),
            )
        duplicated = self.workspace.connection.execute(
            "SELECT COUNT(*) AS c FROM financial_year WHERE label='SHARED-FY'"
        ).fetchone()["c"]
        self.assertEqual(duplicated, 2)

        # Ordinary engagement creation still reuses one exact matching definition.
        first = self.create_company("FIRST-DEMO")
        second = self.create_company("SECOND-DEMO")
        first_year = self.create_year(first, "FY2026", 2026)
        second_year = self.create_year(second, "FY2026", 2026)
        used = [
            row["financial_year_id"]
            for row in self.workspace.connection.execute(
                "SELECT financial_year_id FROM engagement WHERE engagement_id IN (?, ?)",
                (first_year, second_year),
            )
        ]
        self.assertEqual(len(used), 2)
        self.assertEqual(len(set(used)), 1)
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) AS c FROM financial_year WHERE label='FY2026'"
            ).fetchone()["c"],
            1,
        )

    def test_integrity_checks_pass_on_a_populated_workspace(self) -> None:
        self.seed_demo()
        self.assertEqual(self.workspace.connection.execute("PRAGMA integrity_check").fetchone()[0], "ok")
        self.assertEqual(self.workspace.connection.execute("PRAGMA foreign_key_check").fetchall(), [])


class ManifestContractTests(unittest.TestCase):
    """The canonical manifest must match the frozen cross-implementation fixture."""

    def test_fixture_document_digest(self) -> None:
        document = (FIXTURE_DIR / "manifest_v1_example.txt").read_text(encoding="utf-8")
        expected_digest = (FIXTURE_DIR / "manifest_v1_example.sha256").read_text(encoding="utf-8").strip()
        self.assertEqual(sha256_hex(document), expected_digest)

    def test_builder_reproduces_the_fixture_byte_for_byte(self) -> None:
        document = build_manifest_document(
            engagement_id="11111111-1111-4111-8111-111111111111",
            company_id="22222222-2222-4222-8222-222222222222",
            company_legal_name="ABC Manufacturing (Demo) Limited",
            company_short_name="ABC-DEMO",
            financial_year_id="33333333-3333-4333-8333-333333333333",
            financial_year_label="FY2026",
            period_start="2026-01-01",
            period_end="2026-12-31",
            currency_code="USD",
            minor_unit_scale=2,
            prior_engagement_id=None,
            prior_root_digest=None,
            accounts=[
                {"account_code": "4000", "account_name": "Revenue", "account_type": "INCOME",
                 "revision_no": 2, "amount_minor": 85000000000},
                {"account_code": "1200", "account_name": "Trade receivables", "account_type": "ASSET",
                 "revision_no": 1, "amount_minor": 18000000000},
                {"account_code": "1300", "account_name": "Inventory", "account_type": "ASSET",
                 "revision_no": 1, "amount_minor": 24000000000},
                {"account_code": "9999", "account_name": "Suspense | pipe \\ backslash",
                 "account_type": "UNCLASSIFIED", "revision_no": None, "amount_minor": None},
            ],
        )
        self.assertEqual(document, (FIXTURE_DIR / "manifest_v1_example.txt").read_text(encoding="utf-8"))

    def test_digest_is_order_independent(self) -> None:
        accounts = [
            {"account_code": "4000", "account_name": "Revenue", "account_type": "INCOME",
             "revision_no": 1, "amount_minor": 1},
            {"account_code": "1200", "account_name": "Trade receivables", "account_type": "ASSET",
             "revision_no": 1, "amount_minor": 2},
        ]
        common = dict(
            engagement_id="11111111-1111-4111-8111-111111111111",
            company_id="22222222-2222-4222-8222-222222222222",
            company_legal_name="Demo",
            company_short_name="DEMO",
            financial_year_id="33333333-3333-4333-8333-333333333333",
            financial_year_label="FY2026",
            period_start="2026-01-01",
            period_end="2026-12-31",
            currency_code="USD",
            minor_unit_scale=2,
            prior_engagement_id=None,
            prior_root_digest=None,
        )
        first = build_manifest_document(accounts=accounts, **common)
        second = build_manifest_document(accounts=list(reversed(accounts)), **common)
        self.assertEqual(sha256_hex(first), sha256_hex(second))


class RepositoryHygieneTests(unittest.TestCase):
    """ADR-012: only synthetic data may exist in the repository."""

    def test_no_live_workspace_database_is_committed(self) -> None:
        tracked = list(Path(REPO_ROOT).rglob("*.db"))
        tracked = [path for path in tracked if ".git" not in path.parts]
        self.assertEqual(tracked, [])

    def test_demo_dataset_is_visibly_synthetic(self) -> None:
        import demo_data

        self.assertIn("Demo", demo_data.DEMO_COMPANY["legal_name"])
        self.assertIn("DEMO", demo_data.DEMO_COMPANY["short_name"])
        self.assertEqual(demo_data.DEMO_COMPANY["country_code"], "ZZ")


if __name__ == "__main__":
    unittest.main()
