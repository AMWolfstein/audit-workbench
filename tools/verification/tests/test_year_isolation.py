"""Year isolation: each financial year is an independent engagement boundary.

Covers requirements.md invariants 1-5 and mvp-scope.md "Year isolation"
acceptance criteria.
"""

from __future__ import annotations

import unittest

from _harness import ValidationError, WorkspaceTestCase, parse_amount_to_minor


class YearIsolationTests(WorkspaceTestCase):
    def test_two_years_are_separate_engagement_records(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027)

        self.assertNotEqual(fy2026, fy2027)
        engagements = self.workspace.list_engagements(company_id)
        self.assertEqual({row["label"] for row in engagements}, {"FY2026", "FY2027"})

        # The company row itself carries no mutable "current year" column.
        columns = {row[1] for row in self.workspace.connection.execute("PRAGMA table_info(company)")}
        for forbidden in ("current_year", "financial_year", "active_engagement_id", "year"):
            self.assertNotIn(forbidden, columns)

    def test_every_year_owned_row_carries_engagement_id(self) -> None:
        """Invariant 1: year-owned tables must scope rows explicitly."""
        year_owned_tables = ("account", "financial_data", "finalization_manifest")
        for table in year_owned_tables:
            columns = {
                row[1]: row for row in self.workspace.connection.execute(f"PRAGMA table_info({table})")
            }
            self.assertIn("engagement_id", columns, f"{table} must be engagement-owned")
            self.assertEqual(columns["engagement_id"][3], 1, f"{table}.engagement_id must be NOT NULL")

    def test_same_company_cannot_have_duplicate_financial_year(self) -> None:
        company_id = self.create_company()
        self.create_year(company_id, "FY2026", 2026)
        with self.assertRaises(ValidationError) as caught:
            self.create_year(company_id, "FY2026", 2026)
        self.assertIn("already has an engagement", str(caught.exception))

    def test_overlapping_periods_are_rejected(self) -> None:
        company_id = self.create_company()
        self.create_year(company_id, "FY2026", 2026)
        with self.assertRaises(ValidationError):
            self.workspace.create_engagement(
                company_id=company_id,
                label="FY2026-H2",
                period_start="2026-07-01",
                period_end="2026-12-31",
            )

    def test_accounts_and_values_belong_to_exactly_one_year(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027)
        account_2026, _ = self.add_value(fy2026, "4000", "Revenue", "850000000")
        self.add_value(fy2027, "4000", "Revenue", "920000000")

        # The same account code in two years is two independent account rows.
        codes = self.workspace.connection.execute(
            "SELECT engagement_id, COUNT(*) AS c FROM account WHERE account_code = '4000' GROUP BY engagement_id"
        ).fetchall()
        self.assertEqual(len(codes), 2)

        # A value may not reference an account owned by another engagement:
        # the composite ownership foreign key rejects it even via raw SQL.
        with self.assertRaises(Exception) as caught:
            self.raw_sql(
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, recorded_at_utc, recorded_by) "
                "VALUES ('x-1', ?, ?, 1, 1, 'USD', '2027-01-01T00:00:00.000Z', "
                "'00000000-0000-4000-8000-000000000001')",
                (fy2027, account_2026),
            )
        self.assertIn("FOREIGN KEY", str(caught.exception).upper())

    def test_application_rejects_value_for_account_of_another_year(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027)
        account_2026, _ = self.add_value(fy2026, "4000", "Revenue", "850000000")

        with self.assertRaises(ValidationError) as caught:
            self.workspace.record_value(
                engagement_id=fy2027, account_id=account_2026, amount_minor=1
            )
        self.assertIn("does not belong to this engagement", str(caught.exception))

    def test_prior_year_must_be_same_company(self) -> None:
        company_a = self.create_company("AAA-DEMO")
        company_b = self.create_company("BBB-DEMO")
        fy2026_a = self.create_year(company_a, "FY2026", 2026)
        self.add_value(fy2026_a, "4000", "Revenue", "100")
        self.workspace.finalize_engagement(fy2026_a)
        fy2027_b = self.create_year(company_b, "FY2027", 2027)

        with self.assertRaises(ValidationError) as caught:
            self.workspace.link_prior_year(fy2027_b, fy2026_a)
        self.assertIn("same company", str(caught.exception))

    def test_prior_year_must_be_finalized_and_earlier(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027)

        with self.assertRaises(ValidationError) as caught:
            self.workspace.link_prior_year(fy2027, fy2026)
        self.assertIn("finalized", str(caught.exception))

        self.add_value(fy2026, "4000", "Revenue", "100")
        self.workspace.finalize_engagement(fy2026)
        # A later year cannot be the prior year of an earlier one.
        with self.assertRaises(ValidationError):
            self.workspace.link_prior_year(fy2026, fy2027)

    def test_prior_year_relationship_is_immutable(self) -> None:
        info = self.seed_demo()
        fy2027 = info["fy2027_engagement_id"]
        with self.assertRaises(ValidationError) as caught:
            self.workspace.link_prior_year(fy2027, info["fy2026_engagement_id"])
        self.assertIn("already has a prior-year relationship", str(caught.exception))

        for statement, params in (
            ("UPDATE prior_year_relationship SET prior_engagement_id = ? WHERE current_engagement_id = ?",
             (fy2027, fy2027)),
            ("DELETE FROM prior_year_relationship WHERE current_engagement_id = ?", (fy2027,)),
        ):
            with self.assertRaises(Exception) as caught:
                self.raw_sql(statement, params)
            self.assertIn("AWB-GUARD-PRIOR-YEAR-IMMUTABLE", str(caught.exception))

    def test_current_year_edits_do_not_touch_prior_year(self) -> None:
        """FY2027 work must leave FY2026 rows, counts, digest and metadata unchanged."""
        info = self.seed_demo()
        fy2026, fy2027 = info["fy2026_engagement_id"], info["fy2027_engagement_id"]
        before = self.engagement_snapshot(fy2026)

        # Heavy current-year activity: new account, corrections, status change.
        self.add_value(fy2027, "5000", "Cost of sales", "415000000")
        account_4000 = self.workspace.connection.execute(
            "SELECT account_id FROM account WHERE engagement_id = ? AND account_code = '4000'", (fy2027,)
        ).fetchone()["account_id"]
        self.workspace.record_value(
            engagement_id=fy2027,
            account_id=account_4000,
            amount_minor=parse_amount_to_minor("935000000", 2),
            correction_reason="Cut-off adjustment",
        )
        self.workspace.set_engagement_status(fy2027, "IN_PROGRESS")
        self.workspace.comparative_rows(fy2027)

        after = self.engagement_snapshot(fy2026)
        self.assertEqual(before, after)
        self.assertEqual(before["value_count"], 3)
        self.assertTrue(self.workspace.verify_finalization_digest(fy2026))

    def test_prior_year_is_referenced_not_duplicated(self) -> None:
        """FY2027 stores a relationship row, not a copy of the FY2026 database."""
        info = self.seed_demo()
        fy2026, fy2027 = info["fy2026_engagement_id"], info["fy2027_engagement_id"]

        relationship = self.workspace.connection.execute(
            "SELECT * FROM prior_year_relationship WHERE current_engagement_id = ?", (fy2027,)
        ).fetchone()
        self.assertIsNotNone(relationship)
        self.assertEqual(relationship["prior_engagement_id"], fy2026)

        # No FY2026-owned row was copied into FY2027: the only shared key is the code.
        prior_ids = {
            row["account_id"]
            for row in self.workspace.connection.execute(
                "SELECT account_id FROM account WHERE engagement_id = ?", (fy2026,)
            )
        }
        current_ids = {
            row["account_id"]
            for row in self.workspace.connection.execute(
                "SELECT account_id FROM account WHERE engagement_id = ?", (fy2027,)
            )
        }
        self.assertEqual(prior_ids & current_ids, set())


if __name__ == "__main__":
    unittest.main()
