"""Finalization: preflight, atomicity and the read-only guarantee.

Covers audit-year-lifecycle.md section 5 and mvp-scope.md "Finalization"
acceptance criteria. Every protected write is attempted twice: once through the
application command (friendly refusal) and once through raw SQL (database
trigger refusal), because UI disabling alone is never a control (ADR-007).
"""

from __future__ import annotations

import unittest

from _harness import EngagementFinalizedError, ValidationError, WorkspaceTestCase, parse_amount_to_minor


class FinalizationTests(WorkspaceTestCase):
    def setUp(self) -> None:
        super().setUp()
        self.company_id = self.create_company("ABC-DEMO")
        self.fy2026 = self.create_year(self.company_id, "FY2026", 2026)
        self.account_id, _ = self.add_value(self.fy2026, "4000", "Revenue", "850000000")

    # -- preflight and confirmation ----------------------------------------

    def test_preflight_blocks_engagement_without_values(self) -> None:
        empty_year = self.create_year(self.company_id, "FY2025", 2025)
        self.workspace.add_account(engagement_id=empty_year, account_code="4000", account_name="Revenue")
        problems = self.workspace.finalization_preflight(empty_year)
        self.assertTrue(any("no recorded value" in p for p in problems))
        with self.assertRaises(ValidationError):
            self.workspace.finalize_engagement(empty_year)
        self.assertEqual(self.workspace.get_engagement(empty_year)["status"], "DRAFT")

    def test_confirmation_text_must_match_company_and_year(self) -> None:
        with self.assertRaises(ValidationError) as caught:
            self.workspace.finalize_engagement(self.fy2026, confirmation_text="FY2026")
        self.assertIn("ABC-DEMO FY2026", str(caught.exception))
        self.workspace.finalize_engagement(self.fy2026, confirmation_text="ABC-DEMO FY2026")
        self.assertEqual(self.workspace.get_engagement(self.fy2026)["status"], "FINALIZED")

    def test_finalization_records_actor_time_and_digest(self) -> None:
        digest = self.workspace.finalize_engagement(self.fy2026)
        engagement = self.workspace.get_engagement(self.fy2026)
        self.assertEqual(engagement["status"], "FINALIZED")
        self.assertEqual(engagement["finalization_digest"], digest)
        self.assertIsNotNone(engagement["finalized_at_utc"])
        self.assertIsNotNone(engagement["finalized_by"])
        self.assertEqual(engagement["finalization_manifest_version"], "AWB-MANIFEST/1.0")
        self.assertTrue(self.workspace.verify_finalization_digest(self.fy2026))

    def test_finalization_is_recorded_in_the_audit_trail(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)
        events = [e["event_type"] for e in self.workspace.audit_events(engagement_id=self.fy2026)]
        self.assertIn("ENGAGEMENT_FINALIZED", events)
        event = next(
            e for e in self.workspace.audit_events(engagement_id=self.fy2026)
            if e["event_type"] == "ENGAGEMENT_FINALIZED"
        )
        self.assertIn("read-only", event["description"])
        self.assertTrue(self.workspace.verify_audit_chain())

    def test_repeat_finalization_is_rejected(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)
        with self.assertRaises(EngagementFinalizedError):
            self.workspace.finalize_engagement(self.fy2026)
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) AS c FROM finalization_manifest WHERE engagement_id = ?", (self.fy2026,)
            ).fetchone()["c"],
            1,
        )

    def test_failed_finalization_leaves_no_partial_state(self) -> None:
        """A preflight failure inside the transaction rolls manifest + event back."""
        incomplete = self.create_year(self.company_id, "FY2024", 2024)
        self.workspace.add_account(engagement_id=incomplete, account_code="4000", account_name="Revenue")
        events_before = len(self.workspace.audit_events(limit=1000))

        with self.assertRaises(ValidationError):
            self.workspace.finalize_engagement(incomplete)

        engagement = self.workspace.get_engagement(incomplete)
        self.assertEqual(engagement["status"], "DRAFT")
        self.assertIsNone(engagement["finalized_at_utc"])
        self.assertIsNone(engagement["finalization_digest"])
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) AS c FROM finalization_manifest WHERE engagement_id = ?", (incomplete,)
            ).fetchone()["c"],
            0,
        )
        self.assertEqual(len(self.workspace.audit_events(limit=1000)), events_before)

    # -- application-level protection --------------------------------------

    def test_application_refuses_every_write_after_finalization(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)

        with self.assertRaises(EngagementFinalizedError):
            self.workspace.add_account(
                engagement_id=self.fy2026, account_code="9999", account_name="Late account"
            )
        with self.assertRaises(EngagementFinalizedError):
            self.workspace.record_value(
                engagement_id=self.fy2026, account_id=self.account_id, amount_minor=1
            )
        with self.assertRaises(EngagementFinalizedError):
            self.workspace.set_engagement_status(self.fy2026, "IN_PROGRESS")

    def test_no_unfinalize_path_exists(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)
        self.assertFalse(
            any(name.startswith(("unfinalize", "reopen")) for name in dir(self.workspace)),
            "the MVP must not expose an unfinalize or reopen command",
        )
        with self.assertRaises(Exception) as caught:
            self.raw_sql("UPDATE engagement SET status = 'DRAFT' WHERE engagement_id = ?", (self.fy2026,))
        self.assertIn("AWB-GUARD-ENGAGEMENT-FINALIZED", str(caught.exception))

    # -- database-level protection (defence in depth) -----------------------

    def test_database_triggers_reject_direct_writes_to_finalized_year(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)
        actor = "00000000-0000-4000-8000-000000000001"

        attempts = [
            (
                "insert account",
                "INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, "
                "display_order, created_at_utc, created_by) VALUES ('raw-1', ?, 'Z999', 'Raw', 'ASSET', 99, "
                "'2027-01-01T00:00:00.000Z', ?)",
                (self.fy2026, actor),
                "AWB-GUARD-ACCOUNT-FINALIZED",
            ),
            (
                "update account",
                "UPDATE account SET account_name = 'Rewritten' WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-ACCOUNT-FINALIZED",
            ),
            (
                "delete account",
                "DELETE FROM account WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-ACCOUNT",
            ),
            (
                "insert value",
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, supersedes_id, recorded_at_utc, recorded_by) "
                "VALUES ('raw-2', ?, ?, 99, 1, 'USD', NULL, '2027-01-01T00:00:00.000Z', ?)",
                (self.fy2026, self.account_id, actor),
                "AWB-GUARD-FINANCIAL-DATA-FINALIZED",
            ),
            (
                "update value",
                "UPDATE financial_data SET amount_minor = 1 WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY",
            ),
            (
                "delete value",
                "DELETE FROM financial_data WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY",
            ),
            (
                "change engagement identity",
                "UPDATE engagement SET company_id = company_id WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-ENGAGEMENT-FINALIZED",
            ),
            (
                "delete engagement",
                "DELETE FROM engagement WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-ENGAGEMENT-DELETE",
            ),
            (
                "rewrite manifest",
                "UPDATE finalization_manifest SET root_digest = replace(root_digest, 'a', 'b') "
                "WHERE engagement_id = ?",
                (self.fy2026,),
                "AWB-GUARD-MANIFEST-IMMUTABLE",
            ),
        ]

        for label, sql, params, expected in attempts:
            with self.subTest(attempt=label):
                with self.assertRaises(Exception) as caught:
                    self.raw_sql(sql, params)
                self.assertIn(expected, str(caught.exception))

        # Nothing changed.
        self.assertTrue(self.workspace.verify_finalization_digest(self.fy2026))
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) AS c FROM financial_data WHERE engagement_id = ?", (self.fy2026,)
            ).fetchone()["c"],
            1,
        )

    def test_finalized_status_cannot_be_forged_without_a_manifest(self) -> None:
        other = self.create_year(self.company_id, "FY2023", 2023)
        with self.assertRaises(Exception) as caught:
            self.raw_sql(
                "UPDATE engagement SET status = 'FINALIZED', finalized_at_utc = '2027-01-01T00:00:00.000Z', "
                "finalized_by = '00000000-0000-4000-8000-000000000001', "
                "finalization_digest = '" + "0" * 64 + "', finalization_manifest_version = 'AWB-MANIFEST/1.0' "
                "WHERE engagement_id = ?",
                (other,),
            )
        self.assertIn("AWB-GUARD-FINALIZATION-MANIFEST", str(caught.exception))

    def test_finalized_data_remains_readable(self) -> None:
        self.workspace.finalize_engagement(self.fy2026)
        values = self.workspace.latest_values(self.fy2026)
        self.assertEqual(len(values), 1)
        self.assertEqual(values[0]["amount_minor"], parse_amount_to_minor("850000000", 2))

    def test_draft_corrections_append_revisions(self) -> None:
        self.workspace.record_value(
            engagement_id=self.fy2026,
            account_id=self.account_id,
            amount_minor=parse_amount_to_minor("860000000", 2),
            correction_reason="Cut-off adjustment",
        )
        history = self.workspace.value_history(self.fy2026, self.account_id)
        self.assertEqual([row["revision_no"] for row in history], [1, 2])
        self.assertEqual(history[1]["supersedes_id"], history[0]["financial_data_id"])
        self.assertEqual(self.workspace.latest_values(self.fy2026)[0]["revision_no"], 2)


if __name__ == "__main__":
    unittest.main()
