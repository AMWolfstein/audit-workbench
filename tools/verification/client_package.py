"""Dependency-free reference verifier for AWB-CLIENT/1.0 packages.

This verifies the security envelope, canonical manifest, entry lengths/hashes,
and each archived audit event's own hash. Business-data finalization manifests
remain covered by ``workspace.build_manifest_document`` and the .NET importer.
"""
from __future__ import annotations

import hashlib
import json
import sys
import zipfile
from pathlib import Path

FORMAT = "AWB-CLIENT/1.0"
MAX_ENTRY = 50 * 1024 * 1024
MAX_TOTAL = 100 * 1024 * 1024
MAX_RATIO = 200
DATA_PATHS = {
    "data/company.json", "data/financial_years.json", "data/engagements.json",
    "data/accounts.jsonl", "data/financial_data.jsonl", "data/prior_year_relationships.json",
    "data/finalization_manifests.json", "data/principals.json", "history/team_members.json",
    "history/assignments.json", "audit/events.jsonl", "audit/source_chain.json",
}
ALLOWED = DATA_PATHS | {"manifest.json"}


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def audit_hash(event: dict) -> str:
    fields = (
        str(event["sequence_no"]), event["audit_event_id"], event["occurred_at_utc"],
        event["actor_user_id"], event["event_type"], event["outcome"],
        event.get("company_id") or "NONE", event.get("engagement_id") or "NONE",
        event["entity_type"], event["entity_id"], event["description"], event["details_json"],
        event.get("previous_event_hash") or "GENESIS",
    )
    return sha256("|".join(fields).encode("utf-8"))


def verify(path: Path) -> list[str]:
    """Return all detected envelope/integrity problems; an empty list means valid."""
    problems: list[str] = []
    if path.stat().st_size > MAX_TOTAL:
        return ["package exceeds 100 MiB"]
    try:
        with zipfile.ZipFile(path) as package:
            infos = package.infolist()
            names = [entry.filename for entry in infos]
            if len(names) != len(set(names)):
                problems.append("duplicate ZIP entry")
            if set(names) != ALLOWED:
                problems.append("missing or unknown ZIP entry")
            total_uncompressed = 0
            for entry in infos:
                name = entry.filename
                total_uncompressed += entry.file_size
                is_symlink = (entry.external_attr >> 16) & 0xF000 == 0xA000
                if name not in ALLOWED or ".." in name or name.startswith(("/", "\\")) or "\\" in name or is_symlink:
                    problems.append(f"unsafe entry: {name}")
                if entry.file_size > MAX_ENTRY or total_uncompressed > MAX_TOTAL:
                    problems.append(f"entry or package too large: {name}")
                if entry.compress_size and entry.file_size // entry.compress_size > MAX_RATIO:
                    problems.append(f"compression ratio too high: {name}")
            if problems:
                return problems
            entries = {name: package.read(name) for name in names}
    except (OSError, zipfile.BadZipFile) as error:
        return [f"invalid ZIP: {error}"]

    try:
        manifest = json.loads(entries["manifest.json"])
        if canonical_json(manifest) != entries["manifest.json"]:
            problems.append("manifest.json is not canonical JSON")
        if manifest.get("package_format") != FORMAT:
            problems.append("unsupported package_format")
        if manifest.get("hash_algorithm") != "SHA-256":
            problems.append("unsupported hash_algorithm")
        descriptors = manifest.get("files", [])
        if {item.get("path") for item in descriptors} != DATA_PATHS:
            problems.append("manifest file list is incomplete")
        for item in descriptors:
            data = entries.get(item.get("path", ""))
            if data is None or len(data) != item.get("bytes") or sha256(data) != item.get("sha256"):
                problems.append(f"checksum mismatch: {item.get('path')}")
        events = [json.loads(line) for line in entries["audit/events.jsonl"].splitlines() if line.strip()]
        for event in events:
            if audit_hash(event) != event.get("event_hash"):
                problems.append(f"audit hash mismatch: {event.get('audit_event_id')}")
        source = json.loads(entries["audit/source_chain.json"])
        if source.get("subset_count") != len(events) or source.get("verified_at_export") is not True:
            problems.append("source audit-chain declaration is invalid")
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        problems.append(f"invalid package JSON: {error}")
    return problems


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: python3 tools/verification/client_package.py CLIENT.awb", file=sys.stderr)
        return 2
    path = Path(sys.argv[1])
    problems = verify(path)
    if problems:
        print("AWB-CLIENT verification failed:")
        for problem in problems:
            print(f"- {problem}")
        return 1
    print(f"AWB-CLIENT verification passed; package digest {sha256(_manifest(path))}")
    return 0


def _manifest(path: Path) -> bytes:
    with zipfile.ZipFile(path) as package:
        return package.read("manifest.json")


if __name__ == "__main__":
    raise SystemExit(main())
