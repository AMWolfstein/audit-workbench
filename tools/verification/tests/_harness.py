"""Shared base class for the workspace contract tests."""

from __future__ import annotations

import shutil
import sqlite3
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

HARNESS_DIR = Path(__file__).resolve().parents[1]
if str(HARNESS_DIR) not in sys.path:
    sys.path.insert(0, str(HARNESS_DIR))

from workspace import (  # noqa: E402  (path bootstrap must run first)
    ComparativeRow,
    EngagementFinalizedError,
    FixedClock,
    ValidationError,
    Workspace,
    WorkspaceError,
    parse_amount_to_minor,
)
from demo_data import seed_demo_dataset  # noqa: E402

REPO_ROOT = HARNESS_DIR.parents[1]


class WorkspaceTestCase(unittest.TestCase):
    """Creates a real on-disk SQLite workspace per test (never an in-memory substitute)."""

    def setUp(self) -> None:
        self.temp_dir = Path(tempfile.mkdtemp(prefix="awb-verify-"))
        self.clock = FixedClock(datetime(2027, 3, 1, 9, 0, 0, tzinfo=timezone.utc))
        self.workspace = Workspace.create(self.temp_dir / "workspace.db", clock=self.clock)

    def tearDown(self) -> None:
        self.workspace.close()
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    # -- helpers -----------------------------------------------------------

    def seed_demo(self) -> dict:
        return seed_demo_dataset(self.workspace)

    def create_company(self, short_name: str = "DEMO-CO") -> str:
        return self.workspace.create_company(
            legal_name=f"{short_name} (Demo) Limited",
            short_name=short_name,
            industry="Manufacturing",
            country_code="ZZ",
        )

    def create_year(self, company_id: str, label: str, year: int, prior: str | None = None) -> str:
        return self.workspace.create_engagement(
            company_id=company_id,
            label=label,
            period_start=f"{year}-01-01",
            period_end=f"{year}-12-31",
            prior_engagement_id=prior,
        )

    def add_value(self, engagement_id: str, code: str, name: str, amount: str) -> tuple[str, str]:
        account_id = self.workspace.add_account(
            engagement_id=engagement_id, account_code=code, account_name=name
        )
        value_id = self.workspace.record_value(
            engagement_id=engagement_id,
            account_id=account_id,
            amount_minor=parse_amount_to_minor(amount, 2),
        )
        return account_id, value_id

    def raw_sql(self, sql: str, params: tuple = ()) -> None:
        """Execute a statement directly against SQLite, bypassing every application guard."""
        self.workspace.connection.execute(sql, params)

    def engagement_snapshot(self, engagement_id: str) -> dict:
        """Everything about a year that must not change when another year is edited."""
        connection = self.workspace.connection
        engagement = dict(self.workspace.get_engagement(engagement_id))
        rows = [
            dict(row)
            for row in connection.execute(
                "SELECT * FROM financial_data WHERE engagement_id = ? ORDER BY account_id, revision_no",
                (engagement_id,),
            )
        ]
        accounts = [
            dict(row)
            for row in connection.execute(
                "SELECT * FROM account WHERE engagement_id = ? ORDER BY account_code", (engagement_id,)
            )
        ]
        manifest = connection.execute(
            "SELECT * FROM finalization_manifest WHERE engagement_id = ?", (engagement_id,)
        ).fetchone()
        return {
            "engagement": engagement,
            "accounts": accounts,
            "values": rows,
            "value_count": len(rows),
            "manifest": dict(manifest) if manifest else None,
        }


__all__ = [
    "ComparativeRow",
    "EngagementFinalizedError",
    "REPO_ROOT",
    "ValidationError",
    "Workspace",
    "WorkspaceError",
    "WorkspaceTestCase",
    "parse_amount_to_minor",
    "seed_demo_dataset",
    "sqlite3",
]
