"""Backup: consistent, verifiable, and possible without administrator privileges."""

from __future__ import annotations

import os
import sqlite3
import unittest
from pathlib import Path

from _harness import Workspace, WorkspaceTestCase


class BackupTests(WorkspaceTestCase):
    def test_backup_package_is_created_and_verifies(self) -> None:
        info = self.seed_demo()
        package = self.workspace.create_backup(self.temp_dir / "backups")

        self.assertTrue((package / "workspace.db").exists())
        self.assertTrue((package / "backup-manifest.json").exists())
        self.assertEqual(Workspace.verify_backup(package), [])

        # The package records the finalized digest so a restore can be validated.
        import json

        manifest = json.loads((package / "backup-manifest.json").read_text(encoding="utf-8"))
        digests = {entry["engagement_id"]: entry["root_digest"] for entry in manifest["engagement_digests"]}
        self.assertEqual(digests[info["fy2026_engagement_id"]], info["fy2026_digest"])

    def test_backup_requires_no_elevated_privileges(self) -> None:
        """The destination is an ordinary user-writable folder; no admin APIs are used."""
        destination = self.temp_dir / "user folder with spaces" / "backups"
        package = self.workspace.create_backup(destination)
        self.assertTrue(package.exists())
        self.assertTrue(os.access(package, os.W_OK))

    def test_backup_is_transactionally_consistent_and_reopenable(self) -> None:
        info = self.seed_demo()
        package = self.workspace.create_backup(self.temp_dir / "backups")

        restored_path = self.temp_dir / "restored" / "workspace.db"
        restored_path.parent.mkdir(parents=True, exist_ok=True)
        restored_path.write_bytes((package / "workspace.db").read_bytes())

        restored = Workspace(restored_path)
        try:
            self.assertTrue(restored.verify_finalization_digest(info["fy2026_engagement_id"]))
            self.assertTrue(restored.verify_audit_chain())
            rows = {row.account_code: row for row in restored.comparative_rows(info["fy2027_engagement_id"])}
            self.assertEqual(rows["4000"].change_percent_display(), "8.24")
            self.assertEqual(
                restored.get_engagement(info["fy2026_engagement_id"])["status"], "FINALIZED"
            )
        finally:
            restored.close()

    def test_tampered_backup_is_rejected(self) -> None:
        self.seed_demo()
        package = self.workspace.create_backup(self.temp_dir / "backups")

        db_file = package / "workspace.db"
        data = bytearray(db_file.read_bytes())
        data[-1] = (data[-1] + 1) % 256
        db_file.write_bytes(bytes(data))

        problems = Workspace.verify_backup(package)
        self.assertTrue(problems)
        self.assertTrue(any("checksum" in problem for problem in problems))

    def test_missing_manifest_is_rejected(self) -> None:
        self.seed_demo()
        package = self.workspace.create_backup(self.temp_dir / "backups")
        (package / "backup-manifest.json").unlink()
        self.assertEqual(Workspace.verify_backup(package), ["Backup manifest is missing."])

    def test_backup_is_recorded_in_the_audit_trail(self) -> None:
        self.seed_demo()
        package = self.workspace.create_backup(self.temp_dir / "backups")
        event = next(
            event for event in self.workspace.audit_events(limit=1000) if event["event_type"] == "BACKUP_CREATED"
        )
        self.assertEqual(event["entity_id"], package.name)
        self.assertNotIn(str(self.temp_dir), event["details_json"])  # no sensitive path details

    def test_backup_leaves_no_partial_package_on_success(self) -> None:
        self.seed_demo()
        destination = self.temp_dir / "backups"
        self.workspace.create_backup(destination)
        partials = [path for path in Path(destination).iterdir() if path.name.startswith(".")]
        self.assertEqual(partials, [])


if __name__ == "__main__":
    unittest.main()
