#!/usr/bin/env python3
"""Run the Audit Workbench workspace contract verification suite.

    python3 tools/verification/run_verification.py            # run every test
    python3 tools/verification/run_verification.py -v         # verbose
    python3 tools/verification/run_verification.py --demo     # print the demo scenario

The suite executes the shipped SQL migrations, integrity guards and queries in
db/ against real on-disk SQLite workspaces. It requires no network access, no
administrator rights and no third-party packages.
"""

from __future__ import annotations

import argparse
import shutil
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

HARNESS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(HARNESS_DIR))
sys.path.insert(0, str(HARNESS_DIR / "tests"))

from workspace import FixedClock, Workspace, format_amount  # noqa: E402


def run_tests(verbosity: int) -> int:
    loader = unittest.TestLoader()
    suite = loader.discover(str(HARNESS_DIR / "tests"), pattern="test_*.py", top_level_dir=str(HARNESS_DIR / "tests"))
    runner = unittest.TextTestRunner(verbosity=verbosity)
    result = runner.run(suite)
    return 0 if result.wasSuccessful() else 1


def print_demo() -> int:
    """Walk the documented MVP scenario end to end and print the evidence."""
    temp_dir = Path(tempfile.mkdtemp(prefix="awb-demo-"))
    try:
        workspace = Workspace.create(
            temp_dir / "workspace.db", clock=FixedClock(datetime(2027, 3, 1, 9, 0, 0, tzinfo=timezone.utc))
        )
        info = workspace.seed_demo_data()
        engagement = workspace.get_engagement(info["fy2027_engagement_id"])
        prior = workspace.get_engagement(info["fy2026_engagement_id"])
        scale = engagement["minor_unit_scale"]

        print(f"Company          : {engagement['company_legal_name']} ({engagement['company_short_name']})")
        print(f"Prior year       : {prior['label']}  status={prior['status']}  digest={prior['finalization_digest'][:16]}...")
        print(f"Current year     : {engagement['label']}  status={engagement['status']}")
        print()
        header = f"{'Account':<20}{'FY2026':>20}{'FY2027':>20}{'Difference':>20}{'% Change':>12}"
        print(header)
        print("-" * len(header))
        for row in workspace.comparative_rows(info["fy2027_engagement_id"]):
            print(
                f"{row.account_name:<20}"
                f"{format_amount(row.prior_amount_minor, scale):>20}"
                f"{format_amount(row.current_amount_minor, scale):>20}"
                f"{format_amount(row.change_amount_minor, scale):>20}"
                f"{row.change_percent_display():>12}"
            )
        print()
        print(f"FY2026 digest verifies : {workspace.verify_finalization_digest(info['fy2026_engagement_id'])}")
        print(f"Audit hash chain valid : {workspace.verify_audit_chain()}")
        print(f"Audit events recorded  : {len(workspace.audit_events(limit=1000))}")
        workspace.close()
        return 0
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-v", "--verbose", action="store_true", help="verbose test output")
    parser.add_argument("--demo", action="store_true", help="print the synthetic demo scenario instead of testing")
    args = parser.parse_args()
    if args.demo:
        return print_demo()
    return run_tests(2 if args.verbose else 1)


if __name__ == "__main__":
    raise SystemExit(main())
