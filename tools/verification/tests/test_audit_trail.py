"""Audit trail: every important event is recorded, attributable and append-only."""

from __future__ import annotations

import unittest

from _harness import WorkspaceTestCase, parse_amount_to_minor

REQUIRED_EVENT_TYPES = {
    "COMPANY_CREATED",
    "ENGAGEMENT_CREATED",
    "ACCOUNT_CREATED",
    "FINANCIAL_DATA_ADDED",
    "FINANCIAL_DATA_CHANGED",
    "ENGAGEMENT_FINALIZED",
    "PRIOR_YEAR_LINKED",
    "BACKUP_CREATED",
}


class AuditTrailTests(WorkspaceTestCase):
    def test_all_required_events_are_recorded(self) -> None:
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
        self.workspace.create_backup(self.temp_dir / "backups")

        recorded = {event["event_type"] for event in self.workspace.audit_events(limit=1000)}
        self.assertTrue(REQUIRED_EVENT_TYPES.issubset(recorded), REQUIRED_EVENT_TYPES - recorded)

    def test_each_event_has_the_minimum_required_fields(self) -> None:
        self.seed_demo()
        for event in self.workspace.audit_events(limit=1000):
            with self.subTest(event=event["event_type"]):
                self.assertTrue(event["event_type"])
                self.assertRegex(event["occurred_at_utc"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")
                self.assertTrue(event["entity_type"])
                self.assertTrue(event["entity_id"])
                self.assertTrue(event["description"].strip())
                self.assertTrue(event["actor_user_id"])
                self.assertTrue(event["actor_display_name"])

    def test_events_are_written_in_the_same_transaction_as_the_mutation(self) -> None:
        company_id = self.create_company()
        fy2026 = self.create_year(company_id, "FY2026", 2026)
        events_before = len(self.workspace.audit_events(limit=1000))

        # A command that fails validation must leave neither data nor event behind.
        try:
            self.workspace.add_account(engagement_id=fy2026, account_code="", account_name="Invalid")
        except Exception:
            pass

        self.assertEqual(len(self.workspace.audit_events(limit=1000)), events_before)
        self.assertEqual(
            self.workspace.connection.execute(
                "SELECT COUNT(*) AS c FROM account WHERE engagement_id = ?", (fy2026,)
            ).fetchone()["c"],
            0,
        )

    def test_audit_events_cannot_be_updated_or_deleted(self) -> None:
        self.seed_demo()
        for sql in (
            "UPDATE audit_event SET description = 'rewritten'",
            "DELETE FROM audit_event",
        ):
            with self.subTest(sql=sql):
                with self.assertRaises(Exception) as caught:
                    self.raw_sql(sql)
                self.assertIn("AWB-GUARD-AUDIT-APPEND-ONLY", str(caught.exception))

    def test_hash_chain_detects_tampering(self) -> None:
        self.seed_demo()
        self.assertTrue(self.workspace.verify_audit_chain())

        # Simulate an out-of-band edit by an operator with filesystem access:
        # triggers are dropped first, which is exactly the residual risk ADR-007
        # documents. The hash chain must still reveal the change.
        self.workspace.connection.executescript(
            "DROP TRIGGER trg_audit_event_immutable;"
            "UPDATE audit_event SET description = 'rewritten' WHERE sequence_no = 1;"
        )
        self.assertFalse(self.workspace.verify_audit_chain())

    def test_rejected_events_share_the_chain_and_the_vocabulary(self) -> None:
        """Mirrors the .NET RejectionAuditor: a REJECTED event is a normal link in the chain."""
        info = self.seed_demo()
        self.workspace._append_audit_event(
            event_type="PROTECTED_WRITE_REJECTED",
            entity_type="ENGAGEMENT",
            entity_id=info["fy2026_engagement_id"],
            description="A protected write was rejected (AWB-FINALIZED).",
            company_id=info["company_id"],
            engagement_id=info["fy2026_engagement_id"],
            details={"code": "AWB-FINALIZED", "reason": "ENGAGEMENT_FINALIZED"},
            outcome="REJECTED",
        )
        self.workspace.connection.commit()
        self.assertTrue(self.workspace.verify_audit_chain())
        rows = self.workspace.connection.execute(
            "SELECT COUNT(*) AS n, MAX(sequence_no) AS top FROM audit_event"
        ).fetchone()
        self.assertEqual(rows["n"], rows["top"])
        rejected = [e for e in self.workspace.audit_events(limit=1000) if e["outcome"] == "REJECTED"]
        self.assertEqual([e["event_type"] for e in rejected], ["PROTECTED_WRITE_REJECTED"])
        with self.assertRaises(Exception):
            self.raw_sql("UPDATE audit_event SET outcome = 'IGNORED' WHERE outcome = 'REJECTED'")

    def test_event_scope_links_company_and_engagement(self) -> None:
        info = self.seed_demo()
        finalization = next(
            event
            for event in self.workspace.audit_events(engagement_id=info["fy2026_engagement_id"])
            if event["event_type"] == "ENGAGEMENT_FINALIZED"
        )
        self.assertEqual(finalization["company_id"], info["company_id"])
        self.assertEqual(finalization["engagement_id"], info["fy2026_engagement_id"])
        self.assertEqual(finalization["entity_type"], "ENGAGEMENT")
        self.assertEqual(finalization["entity_id"], info["fy2026_engagement_id"])

    def test_audit_details_do_not_contain_amounts(self) -> None:
        """NFR-14: audit metadata identifies records; it does not reproduce evidence."""
        info = self.seed_demo()
        for event in self.workspace.audit_events(limit=1000):
            if event["event_type"].startswith("FINANCIAL_DATA"):
                self.assertNotIn("amount", event["details_json"])
                self.assertNotIn("850000000", event["details_json"])
                self.assertNotIn("850000000", event["description"])
        self.assertIsNotNone(info["company_id"])


if __name__ == "__main__":
    unittest.main()
