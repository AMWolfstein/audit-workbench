"""Comparative view: FY2027 reads FY2026 through the explicit relationship only.

Covers mvp-scope.md section 4 (expected demo output) and data-model.md section 5.
"""

from __future__ import annotations

import unittest
from decimal import Decimal

from _harness import WorkspaceTestCase, parse_amount_to_minor


class ComparativeTests(WorkspaceTestCase):
    def test_demo_scenario_matches_documented_output(self) -> None:
        info = self.seed_demo()
        rows = {row.account_code: row for row in self.workspace.comparative_rows(info["fy2027_engagement_id"])}

        expected = {
            "4000": ("850000000", "920000000", "70000000", "8.24"),
            "1200": ("180000000", "210000000", "30000000", "16.67"),
            "1300": ("240000000", "275000000", "35000000", "14.58"),
        }
        for code, (prior, current, change, percent) in expected.items():
            with self.subTest(account=code):
                row = rows[code]
                self.assertEqual(row.prior_amount_minor, parse_amount_to_minor(prior, 2))
                self.assertEqual(row.current_amount_minor, parse_amount_to_minor(current, 2))
                self.assertEqual(row.change_amount_minor, parse_amount_to_minor(change, 2))
                self.assertEqual(row.change_percent_display(), percent)

    def test_comparison_does_not_modify_the_prior_year(self) -> None:
        info = self.seed_demo()
        fy2026 = info["fy2026_engagement_id"]
        before = self.engagement_snapshot(fy2026)
        events_before = len(self.workspace.audit_events(limit=1000))

        for _ in range(3):
            self.workspace.comparative_rows(info["fy2027_engagement_id"])

        self.assertEqual(self.engagement_snapshot(fy2026), before)
        self.assertEqual(len(self.workspace.audit_events(limit=1000)), events_before)
        self.assertTrue(self.workspace.verify_finalization_digest(fy2026))

    def test_comparison_uses_the_recorded_relationship_not_year_minus_one(self) -> None:
        """Without a relationship there is no prior column, even if an earlier year exists."""
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        self.add_value(fy2026, "4000", "Revenue", "850000000")
        self.workspace.finalize_engagement(fy2026)

        unlinked_fy2027 = self.create_year(company_id, "FY2027", 2027)
        self.add_value(unlinked_fy2027, "4000", "Revenue", "920000000")

        rows = self.workspace.comparative_rows(unlinked_fy2027)
        self.assertEqual(len(rows), 1)
        self.assertIsNone(rows[0].prior_amount_minor)
        self.assertTrue(rows[0].is_new_account)
        self.assertEqual(rows[0].change_percent_display(), "N/A")

    def test_zero_prior_amount_yields_not_available_percentage(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        self.add_value(fy2026, "4000", "Revenue", "0")
        self.workspace.finalize_engagement(fy2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027, prior=fy2026)
        self.add_value(fy2027, "4000", "Revenue", "500")

        row = self.workspace.comparative_rows(fy2027)[0]
        self.assertEqual(row.change_amount_minor, parse_amount_to_minor("500", 2))
        self.assertIsNone(row.change_percent)
        self.assertEqual(row.change_percent_display(), "N/A")

    def test_new_and_missing_accounts_are_labelled(self) -> None:
        info = self.seed_demo()
        fy2027 = info["fy2027_engagement_id"]
        self.add_value(fy2027, "5000", "Cost of sales", "415000000")

        rows = {row.account_code: row for row in self.workspace.comparative_rows(fy2027)}
        self.assertTrue(rows["5000"].is_new_account)
        self.assertFalse(rows["5000"].is_missing_in_current)
        self.assertEqual(rows["5000"].change_percent_display(), "N/A")

        # An account that only exists in the prior year is still listed.
        self.assertNotIn("6000", rows)

    def test_negative_prior_amount_uses_absolute_denominator(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        self.add_value(fy2026, "8000", "Accumulated loss", "-200")
        self.workspace.finalize_engagement(fy2026)
        fy2027 = self.create_year(company_id, "FY2027", 2027, prior=fy2026)
        self.add_value(fy2027, "8000", "Accumulated loss", "-150")

        row = self.workspace.comparative_rows(fy2027)[0]
        self.assertEqual(row.change_amount_minor, parse_amount_to_minor("50", 2))
        self.assertEqual(row.change_percent, Decimal("25"))
        self.assertEqual(row.change_percent_display(), "25.00")

    def test_comparison_resolves_the_latest_revision_in_each_year(self) -> None:
        info = self.seed_demo()
        fy2027 = info["fy2027_engagement_id"]
        account_id = self.workspace.connection.execute(
            "SELECT account_id FROM account WHERE engagement_id = ? AND account_code = '4000'", (fy2027,)
        ).fetchone()["account_id"]
        self.workspace.record_value(
            engagement_id=fy2027,
            account_id=account_id,
            amount_minor=parse_amount_to_minor("935000000", 2),
            correction_reason="Cut-off adjustment",
        )

        row = {r.account_code: r for r in self.workspace.comparative_rows(fy2027)}["4000"]
        self.assertEqual(row.current_revision_no, 2)
        self.assertEqual(row.current_amount_minor, parse_amount_to_minor("935000000", 2))
        self.assertEqual(row.prior_revision_no, 1)
        self.assertEqual(row.prior_amount_minor, parse_amount_to_minor("850000000", 2))

    def test_amounts_are_exact_integers_never_floating_point(self) -> None:
        types = {
            row[1]: row[2]
            for row in self.workspace.connection.execute("PRAGMA table_info(financial_data)")
        }
        self.assertEqual(types["amount_minor"], "INTEGER")
        self.assertEqual(parse_amount_to_minor("0.1", 2) + parse_amount_to_minor("0.2", 2), 30)


if __name__ == "__main__":
    unittest.main()
