"""Cross-runtime workspace contract harness for Audit Workbench.

This module is **not** the product. The product is the .NET 8 application in
``src/``. This harness exists because the architecture places the year-isolation
and finalization guarantees in the database itself (ADR-007), so those
guarantees must be executable and testable independently of the .NET runtime.

It loads the *shipped* artefacts:

* ``db/migrations/*.sql``  - the schema and integrity guards the application applies
* ``db/sql/*.sql``         - the comparative / latest-value / finalization queries
* ``docs/finalization-manifest.md`` - implemented here as ``build_manifest_document``

and reproduces the application's transaction boundaries (one command = one
transaction containing the business mutation and its audit event).

Standard library only: no network access and no third-party packages are
required to verify a workspace.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sqlite3
import uuid
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from typing import Iterable, Optional, Sequence

REPO_ROOT = Path(__file__).resolve().parents[2]
MIGRATIONS_DIR = REPO_ROOT / "db" / "migrations"
QUERY_DIR = REPO_ROOT / "db" / "sql"

MANIFEST_VERSION = "AWB-MANIFEST/1.0"
SCHEMA_VERSION = "0005_client_handover"
WORKSPACE_FORMAT_VERSION = "1"

OPEN_STATUSES = ("DRAFT", "IN_PROGRESS")
GUARD_PREFIX = "AWB-GUARD-"


class WorkspaceError(Exception):
    """Base error for refused operations (maps to a safe UI message)."""

    code = "AWB-ERROR"


class ValidationError(WorkspaceError):
    code = "AWB-VALIDATION"


class EngagementFinalizedError(WorkspaceError):
    """Raised when a caller tries to change a finalized engagement."""

    code = "AWB-FINALIZED"


class ConcurrencyError(WorkspaceError):
    code = "AWB-CONCURRENCY"


# ---------------------------------------------------------------------------
# Clock and actor abstractions (mirrors IClock / ICurrentActor in the app)
# ---------------------------------------------------------------------------


class Clock:
    """UTC clock. Deterministic in tests, real in normal use."""

    def now_iso(self) -> str:
        return _to_iso(datetime.now(timezone.utc))


class FixedClock(Clock):
    """Deterministic clock: each read advances by a fixed step."""

    def __init__(self, start: datetime, step_seconds: int = 1) -> None:
        self._current = start.astimezone(timezone.utc)
        self._step = timedelta(seconds=step_seconds)

    def now_iso(self) -> str:
        value = _to_iso(self._current)
        self._current = self._current + self._step
        return value


def _to_iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + f"{value.microsecond // 1000:03d}Z"


@dataclass(frozen=True)
class Actor:
    user_id: str
    username: str
    display_name: str


LOCAL_ACTOR = Actor(
    user_id="00000000-0000-4000-8000-000000000001",
    username="local.auditor",
    display_name="Local Auditor (non-production local identity)",
)


# ---------------------------------------------------------------------------
# Canonical manifest (docs/finalization-manifest.md)
# ---------------------------------------------------------------------------


def escape_manifest_field(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace("|", "\\p")
        .replace("\n", "\\n")
        .replace("\r", "\\r")
    )


def build_manifest_document(
    *,
    engagement_id: str,
    company_id: str,
    company_legal_name: str,
    company_short_name: str,
    financial_year_id: str,
    financial_year_label: str,
    period_start: str,
    period_end: str,
    currency_code: str,
    minor_unit_scale: int,
    prior_engagement_id: Optional[str],
    prior_root_digest: Optional[str],
    accounts: Sequence[dict],
) -> str:
    """Build the canonical manifest text defined in docs/finalization-manifest.md."""

    ordered = sorted(accounts, key=lambda a: a["account_code"])
    value_count = sum(1 for a in ordered if a.get("revision_no") is not None)
    lines = [
        MANIFEST_VERSION,
        f"engagement_id={engagement_id}",
        f"company_id={company_id}",
        f"company_legal_name={escape_manifest_field(company_legal_name)}",
        f"company_short_name={escape_manifest_field(company_short_name)}",
        f"financial_year_id={financial_year_id}",
        f"financial_year_label={escape_manifest_field(financial_year_label)}",
        f"period_start={period_start}",
        f"period_end={period_end}",
        f"currency_code={currency_code}",
        f"minor_unit_scale={minor_unit_scale}",
        f"prior_engagement_id={prior_engagement_id or 'NONE'}",
        f"prior_root_digest={prior_root_digest or 'NONE'}",
        f"account_count={len(ordered)}",
        f"value_count={value_count}",
    ]
    for account in ordered:
        revision = account.get("revision_no")
        amount = account.get("amount_minor")
        lines.append(
            "account|{code}|{name}|{type}|{revision}|{amount}".format(
                code=escape_manifest_field(account["account_code"]),
                name=escape_manifest_field(account["account_name"]),
                type=account["account_type"],
                revision="NONE" if revision is None else int(revision),
                amount="NONE" if amount is None else int(amount),
            )
        )
    lines.append("END")
    return "\n".join(lines) + "\n"


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# Comparison arithmetic (exact, no binary floating point - ADR-009)
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ComparativeRow:
    account_code: str
    account_name: str
    prior_amount_minor: Optional[int]
    current_amount_minor: Optional[int]
    prior_revision_no: Optional[int]
    current_revision_no: Optional[int]
    is_new_account: bool
    is_missing_in_current: bool

    @property
    def change_amount_minor(self) -> Optional[int]:
        if self.prior_amount_minor is None or self.current_amount_minor is None:
            return None
        return self.current_amount_minor - self.prior_amount_minor

    @property
    def change_percent(self) -> Optional[Decimal]:
        """Percentage change, or None when it is not defined (displayed as N/A)."""
        change = self.change_amount_minor
        if change is None or not self.prior_amount_minor:
            return None
        return (Decimal(change) / abs(Decimal(self.prior_amount_minor))) * Decimal(100)

    def change_percent_display(self) -> str:
        percent = self.change_percent
        if percent is None:
            return "N/A"
        return str(percent.quantize(Decimal("0.01"), rounding=ROUND_HALF_UP))


def format_amount(amount_minor: Optional[int], scale: int) -> str:
    if amount_minor is None:
        return ""
    value = Decimal(amount_minor) / (Decimal(10) ** scale)
    return f"{value:,.{scale}f}"


def parse_amount_to_minor(text: str, scale: int) -> int:
    """Parse user input such as '850000000' or '-1,234.56' into exact minor units."""
    cleaned = str(text).strip().replace(",", "").replace(" ", "")
    if not cleaned:
        raise ValidationError("Amount is required.")
    if not re.fullmatch(r"[+-]?\d+(\.\d+)?", cleaned):
        raise ValidationError(f"'{text}' is not a valid amount.")
    value = Decimal(cleaned)
    scaled = value.scaleb(scale)
    if scaled != scaled.to_integral_value():
        raise ValidationError(f"'{text}' has more decimal places than the engagement scale ({scale}).")
    minor = int(scaled)
    if not (-(2**63) <= minor < 2**63):
        raise ValidationError("Amount exceeds the supported 64-bit minor-unit range.")
    return minor


# ---------------------------------------------------------------------------
# Workspace
# ---------------------------------------------------------------------------


def _load_sql(name: str) -> str:
    return (QUERY_DIR / name).read_text(encoding="utf-8")


def new_id() -> str:
    return str(uuid.uuid4())


class Workspace:
    """An opened SQLite workspace with the application's command semantics."""

    def __init__(self, db_path: Path, clock: Optional[Clock] = None, actor: Actor = LOCAL_ACTOR) -> None:
        self.db_path = Path(db_path)
        self.clock = clock or Clock()
        self.actor = actor
        self.connection = sqlite3.connect(str(self.db_path), isolation_level=None)
        self.connection.row_factory = sqlite3.Row
        self._apply_connection_policy()

    # -- lifecycle ---------------------------------------------------------

    def _apply_connection_policy(self) -> None:
        cur = self.connection
        cur.execute("PRAGMA foreign_keys = ON")
        cur.execute("PRAGMA busy_timeout = 5000")
        cur.execute("PRAGMA journal_mode = WAL")
        cur.execute("PRAGMA synchronous = FULL")

    def close(self) -> None:
        self.connection.close()

    def __enter__(self) -> "Workspace":
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()

    @classmethod
    def create(cls, db_path: Path, clock: Optional[Clock] = None, actor: Actor = LOCAL_ACTOR) -> "Workspace":
        workspace = cls(db_path, clock=clock, actor=actor)
        workspace.apply_migrations()
        workspace.ensure_local_actor()
        return workspace

    # -- migrations --------------------------------------------------------

    def apply_migrations(self) -> list[str]:
        applied: list[str] = []
        files = sorted(MIGRATIONS_DIR.glob("*.sql"))
        if not files:
            raise WorkspaceError(f"No migrations found in {MIGRATIONS_DIR}")

        done: dict[str, str] = {}
        if self._migration_table_exists():
            done = {
                row["migration_id"]: row["checksum_sha256"]
                for row in self.connection.execute("SELECT migration_id, checksum_sha256 FROM schema_migration")
            }

        for path in files:
            migration_id = path.stem
            script = path.read_text(encoding="utf-8")
            checksum = hashlib.sha256(script.encode("utf-8")).hexdigest()
            if migration_id in done:
                if done[migration_id] != checksum:
                    raise WorkspaceError(
                        f"Migration {migration_id} changed after it was applied; refusing to open the workspace."
                    )
                continue
            # Each migration is applied as one DDL transaction together with its
            # own ledger row, so a half-applied migration cannot be committed.
            ledger_insert = (
                "INSERT INTO schema_migration (migration_id, checksum_sha256, applied_at_utc) VALUES ("
                f"{_sql_literal(migration_id)}, {_sql_literal(checksum)}, {_sql_literal(self.clock.now_iso())});"
            )
            self.connection.executescript("BEGIN;\n" + script + "\n" + ledger_insert + "\nCOMMIT;\n")
            applied.append(migration_id)

        self.connection.execute("BEGIN")
        try:
            for key, value in (
                ("schema_version", SCHEMA_VERSION),
                ("workspace_format_version", WORKSPACE_FORMAT_VERSION),
            ):
                self.connection.execute(
                    "INSERT INTO workspace_metadata (metadata_key, metadata_value) VALUES (?, ?) "
                    "ON CONFLICT (metadata_key) DO UPDATE SET metadata_value = excluded.metadata_value",
                    (key, value),
                )
            self.connection.execute("COMMIT")
        except Exception:
            self.connection.execute("ROLLBACK")
            raise

        # executescript() commits implicitly, so re-assert the connection policy.
        self._apply_connection_policy()
        return applied

    def _migration_table_exists(self) -> bool:
        row = self.connection.execute(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migration'"
        ).fetchone()
        return row is not None

    def ensure_local_actor(self) -> Actor:
        row = self.connection.execute(
            "SELECT user_id FROM app_user WHERE user_id = ?", (self.actor.user_id,)
        ).fetchone()
        if row is None:
            self.connection.execute(
                "INSERT INTO app_user (user_id, username, display_name, status, is_local_demo, created_at_utc) "
                "VALUES (?, ?, ?, 'ACTIVE', 1, ?)",
                (self.actor.user_id, self.actor.username, self.actor.display_name, self.clock.now_iso()),
            )
        return self.actor

    # -- transaction helper -------------------------------------------------

    def transaction(self) -> "_Transaction":
        return _Transaction(self.connection)

    # -- audit trail --------------------------------------------------------

    def _append_audit_event(
        self,
        *,
        event_type: str,
        entity_type: str,
        entity_id: str,
        description: str,
        company_id: Optional[str] = None,
        engagement_id: Optional[str] = None,
        details: Optional[dict] = None,
        outcome: str = "SUCCESS",
    ) -> str:
        row = self.connection.execute(
            "SELECT sequence_no, event_hash FROM audit_event ORDER BY sequence_no DESC LIMIT 1"
        ).fetchone()
        sequence_no = (row["sequence_no"] + 1) if row else 1
        previous_hash = row["event_hash"] if row else None
        event_id = new_id()
        occurred_at = self.clock.now_iso()
        details_json = json.dumps(details or {}, sort_keys=True, separators=(",", ":"))
        canonical = "|".join(
            [
                str(sequence_no),
                event_id,
                occurred_at,
                self.actor.user_id,
                event_type,
                outcome,
                company_id or "NONE",
                engagement_id or "NONE",
                entity_type,
                entity_id,
                description,
                details_json,
                previous_hash or "GENESIS",
            ]
        )
        event_hash = sha256_hex(canonical)
        self.connection.execute(
            """
            INSERT INTO audit_event (
                audit_event_id, sequence_no, occurred_at_utc, actor_user_id, actor_display_name,
                event_type, outcome, company_id, engagement_id, entity_type, entity_id,
                description, details_json, previous_event_hash, event_hash)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                event_id,
                sequence_no,
                occurred_at,
                self.actor.user_id,
                self.actor.display_name,
                event_type,
                outcome,
                company_id,
                engagement_id,
                entity_type,
                entity_id,
                description,
                details_json,
                previous_hash,
                event_hash,
            ),
        )
        return event_id

    def verify_audit_chain(self) -> bool:
        previous_hash = None
        for row in self.connection.execute("SELECT * FROM audit_event ORDER BY sequence_no"):
            canonical = "|".join(
                [
                    str(row["sequence_no"]),
                    row["audit_event_id"],
                    row["occurred_at_utc"],
                    row["actor_user_id"],
                    row["event_type"],
                    row["outcome"],
                    row["company_id"] or "NONE",
                    row["engagement_id"] or "NONE",
                    row["entity_type"],
                    row["entity_id"],
                    row["description"],
                    row["details_json"],
                    previous_hash or "GENESIS",
                ]
            )
            if sha256_hex(canonical) != row["event_hash"]:
                return False
            if (row["previous_event_hash"] or None) != previous_hash:
                return False
            previous_hash = row["event_hash"]
        return True

    # -- companies ----------------------------------------------------------

    def create_company(
        self,
        *,
        legal_name: str,
        short_name: str,
        industry: str,
        country_code: str,
        tax_reference: Optional[str] = None,
    ) -> str:
        legal_name = (legal_name or "").strip()
        short_name = (short_name or "").strip()
        industry = (industry or "").strip()
        country_code = (country_code or "").strip().upper()
        tax_reference = (tax_reference or "").strip() or None
        if not legal_name:
            raise ValidationError("Legal name is required.")
        if not short_name:
            raise ValidationError("Short name is required.")
        if not industry:
            raise ValidationError("Industry is required.")
        if len(country_code) != 2:
            raise ValidationError("Country must be a two-letter ISO 3166-1 alpha-2 code.")

        company_id = new_id()
        with self.transaction():
            duplicate = self.connection.execute(
                "SELECT 1 FROM company WHERE lower(short_name) = lower(?)", (short_name,)
            ).fetchone()
            if duplicate:
                raise ValidationError(f"Short name '{short_name}' is already used by another company.")
            self.connection.execute(
                "INSERT INTO company (company_id, legal_name, short_name, industry, country_code, "
                "tax_reference, status, created_at_utc, created_by, row_version) "
                "VALUES (?, ?, ?, ?, ?, ?, 'ACTIVE', ?, ?, 1)",
                (
                    company_id,
                    legal_name,
                    short_name,
                    industry,
                    country_code,
                    tax_reference,
                    self.clock.now_iso(),
                    self.actor.user_id,
                ),
            )
            self._append_audit_event(
                event_type="COMPANY_CREATED",
                entity_type="COMPANY",
                entity_id=company_id,
                company_id=company_id,
                description=f"Company '{legal_name}' created.",
                details={"short_name": short_name, "country_code": country_code},
            )
        return company_id

    def list_companies(self) -> list[sqlite3.Row]:
        return list(
            self.connection.execute(
                """
                SELECT c.*,
                       (SELECT COUNT(*) FROM engagement e WHERE e.company_id = c.company_id) AS engagement_count
                FROM company c
                ORDER BY c.legal_name
                """
            )
        )

    def get_company(self, company_id: str) -> sqlite3.Row:
        row = self.connection.execute("SELECT * FROM company WHERE company_id = ?", (company_id,)).fetchone()
        if row is None:
            raise ValidationError("Company not found.")
        return row

    # -- engagements --------------------------------------------------------

    def create_engagement(
        self,
        *,
        company_id: str,
        label: str,
        period_start: str,
        period_end: str,
        currency_code: str = "USD",
        minor_unit_scale: int = 2,
        prior_engagement_id: Optional[str] = None,
        status: str = "DRAFT",
    ) -> str:
        label = (label or "").strip()
        if not label:
            raise ValidationError("Financial year label is required.")
        if status not in OPEN_STATUSES:
            raise ValidationError("A new engagement must start as DRAFT or IN_PROGRESS.")
        _require_iso_date(period_start, "Period start")
        _require_iso_date(period_end, "Period end")
        if period_start > period_end:
            raise ValidationError("Period start must not be after period end.")

        engagement_id = new_id()
        with self.transaction():
            company = self.connection.execute(
                "SELECT * FROM company WHERE company_id = ?", (company_id,)
            ).fetchone()
            if company is None:
                raise ValidationError("Company not found.")
            if company["status"] != "ACTIVE":
                raise ValidationError("An archived company cannot receive new engagements.")

            financial_year_id = self._ensure_financial_year(label, period_start, period_end)
            clash = self.connection.execute(
                "SELECT 1 FROM engagement WHERE company_id = ? AND financial_year_id = ?",
                (company_id, financial_year_id),
            ).fetchone()
            if clash:
                raise ValidationError(
                    f"{company['legal_name']} already has an engagement for {label}. "
                    "A financial year is never re-used; create a different year."
                )
            overlap = self.connection.execute(
                """
                SELECT fy.label FROM engagement e
                JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id
                WHERE e.company_id = ? AND fy.period_start <= ? AND fy.period_end >= ?
                """,
                (company_id, period_end, period_start),
            ).fetchone()
            if overlap:
                raise ValidationError(
                    f"The period overlaps the existing engagement '{overlap['label']}' for this company."
                )

            self.connection.execute(
                "INSERT INTO engagement (engagement_id, company_id, financial_year_id, status, currency_code, "
                "minor_unit_scale, created_at_utc, created_by, row_version) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1)",
                (
                    engagement_id,
                    company_id,
                    financial_year_id,
                    status,
                    currency_code.upper(),
                    minor_unit_scale,
                    self.clock.now_iso(),
                    self.actor.user_id,
                ),
            )
            self._append_audit_event(
                event_type="ENGAGEMENT_CREATED",
                entity_type="ENGAGEMENT",
                entity_id=engagement_id,
                company_id=company_id,
                engagement_id=engagement_id,
                description=f"Engagement {label} created for {company['legal_name']}.",
                details={"label": label, "period_start": period_start, "period_end": period_end, "status": status},
            )
            if prior_engagement_id:
                self._link_prior_year(engagement_id, prior_engagement_id)
        return engagement_id

    def _ensure_financial_year(self, label: str, period_start: str, period_end: str) -> str:
        row = self.connection.execute(
            "SELECT financial_year_id FROM financial_year WHERE label = ? AND period_start = ? AND period_end = ?",
            (label, period_start, period_end),
        ).fetchone()
        if row:
            return row["financial_year_id"]
        financial_year_id = new_id()
        self.connection.execute(
            "INSERT INTO financial_year (financial_year_id, label, period_start, period_end, created_at_utc) "
            "VALUES (?, ?, ?, ?, ?)",
            (financial_year_id, label, period_start, period_end, self.clock.now_iso()),
        )
        return financial_year_id

    def _link_prior_year(self, current_engagement_id: str, prior_engagement_id: str) -> str:
        current = self.get_engagement(current_engagement_id)
        prior = self.connection.execute(
            "SELECT e.*, fy.label AS label, fy.period_end AS period_end FROM engagement e "
            "JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id WHERE e.engagement_id = ?",
            (prior_engagement_id,),
        ).fetchone()
        if prior is None:
            raise ValidationError("The selected prior engagement does not exist.")
        if prior_engagement_id == current_engagement_id:
            raise ValidationError("An engagement cannot be its own prior year.")
        if prior["company_id"] != current["company_id"]:
            raise ValidationError("The prior engagement must belong to the same company.")
        if prior["status"] != "FINALIZED":
            raise ValidationError("Only a finalized engagement can be selected as the prior year.")
        if prior["period_end"] >= current["period_end"]:
            raise ValidationError("The prior engagement period must end before the current period.")
        if current["status"] == "FINALIZED":
            raise EngagementFinalizedError("A finalized engagement cannot gain a prior-year link.")
        existing = self.connection.execute(
            "SELECT 1 FROM prior_year_relationship WHERE current_engagement_id = ?", (current_engagement_id,)
        ).fetchone()
        if existing:
            raise ValidationError("This engagement already has a prior-year relationship; it is immutable.")

        relationship_id = new_id()
        self.connection.execute(
            "INSERT INTO prior_year_relationship (relationship_id, current_engagement_id, prior_engagement_id, "
            "linked_at_utc, linked_by) VALUES (?, ?, ?, ?, ?)",
            (relationship_id, current_engagement_id, prior_engagement_id, self.clock.now_iso(), self.actor.user_id),
        )
        self._append_audit_event(
            event_type="PRIOR_YEAR_LINKED",
            entity_type="PRIOR_YEAR_RELATIONSHIP",
            entity_id=relationship_id,
            company_id=current["company_id"],
            engagement_id=current_engagement_id,
            description=f"Prior-year relationship created to {prior['label']} (read-only source).",
            details={"prior_engagement_id": prior_engagement_id, "prior_digest": prior["finalization_digest"]},
        )
        return relationship_id

    def link_prior_year(self, current_engagement_id: str, prior_engagement_id: str) -> str:
        with self.transaction():
            return self._link_prior_year(current_engagement_id, prior_engagement_id)

    def get_engagement(self, engagement_id: str) -> sqlite3.Row:
        row = self.connection.execute(
            """
            SELECT e.*, fy.label AS label, fy.period_start AS period_start, fy.period_end AS period_end,
                   c.legal_name AS company_legal_name, c.short_name AS company_short_name,
                   r.prior_engagement_id AS prior_engagement_id
            FROM engagement e
            JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id
            JOIN company c ON c.company_id = e.company_id
            LEFT JOIN prior_year_relationship r ON r.current_engagement_id = e.engagement_id
            WHERE e.engagement_id = ?
            """,
            (engagement_id,),
        ).fetchone()
        if row is None:
            raise ValidationError("Engagement not found.")
        return row

    def list_engagements(self, company_id: str) -> list[sqlite3.Row]:
        return list(
            self.connection.execute(
                """
                SELECT e.*, fy.label AS label, fy.period_start AS period_start, fy.period_end AS period_end,
                       r.prior_engagement_id AS prior_engagement_id
                FROM engagement e
                JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id
                LEFT JOIN prior_year_relationship r ON r.current_engagement_id = e.engagement_id
                WHERE e.company_id = ?
                ORDER BY fy.period_end DESC
                """,
                (company_id,),
            )
        )

    def eligible_prior_engagements(self, company_id: str, period_end: str) -> list[sqlite3.Row]:
        return list(
            self.connection.execute(
                """
                SELECT e.engagement_id, fy.label AS label, fy.period_end AS period_end, e.finalization_digest
                FROM engagement e
                JOIN financial_year fy ON fy.financial_year_id = e.financial_year_id
                WHERE e.company_id = ? AND e.status = 'FINALIZED' AND fy.period_end < ?
                ORDER BY fy.period_end DESC
                """,
                (company_id, period_end),
            )
        )

    def set_engagement_status(self, engagement_id: str, new_status: str, expected_row_version: Optional[int] = None) -> None:
        if new_status not in OPEN_STATUSES:
            raise ValidationError("Use finalize_engagement() to finalize an engagement.")
        with self.transaction():
            engagement = self._load_open_engagement(engagement_id, expected_row_version)
            self.connection.execute(
                "UPDATE engagement SET status = ?, row_version = row_version + 1 WHERE engagement_id = ?",
                (new_status, engagement_id),
            )
            self._append_audit_event(
                event_type="ENGAGEMENT_STATUS_CHANGED",
                entity_type="ENGAGEMENT",
                entity_id=engagement_id,
                company_id=engagement["company_id"],
                engagement_id=engagement_id,
                description=f"Engagement status changed from {engagement['status']} to {new_status}.",
                details={"from": engagement["status"], "to": new_status},
            )

    def _load_open_engagement(self, engagement_id: str, expected_row_version: Optional[int] = None) -> sqlite3.Row:
        engagement = self.get_engagement(engagement_id)
        if engagement["status"] == "FINALIZED":
            raise EngagementFinalizedError(
                f"{engagement['label']} was finalized on {engagement['finalized_at_utc']} and is read-only."
            )
        if expected_row_version is not None and engagement["row_version"] != expected_row_version:
            raise ConcurrencyError("This engagement changed in another window. Reload and try again.")
        return engagement

    # -- accounts and financial data ----------------------------------------

    def add_account(
        self,
        *,
        engagement_id: str,
        account_code: str,
        account_name: str,
        account_type: str = "UNCLASSIFIED",
        display_order: Optional[int] = None,
    ) -> str:
        account_code = (account_code or "").strip().upper()
        account_name = (account_name or "").strip()
        if not account_code:
            raise ValidationError("Account code is required.")
        if not account_name:
            raise ValidationError("Account name is required.")

        account_id = new_id()
        with self.transaction():
            engagement = self._load_open_engagement(engagement_id)
            duplicate = self.connection.execute(
                "SELECT 1 FROM account WHERE engagement_id = ? AND account_code = ?",
                (engagement_id, account_code),
            ).fetchone()
            if duplicate:
                raise ValidationError(f"Account code '{account_code}' already exists in this financial year.")
            if display_order is None:
                row = self.connection.execute(
                    "SELECT COALESCE(MAX(display_order), 0) + 10 AS next FROM account WHERE engagement_id = ?",
                    (engagement_id,),
                ).fetchone()
                order = row["next"]
            else:
                order = display_order
            self.connection.execute(
                "INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, "
                "display_order, created_at_utc, created_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    account_id,
                    engagement_id,
                    account_code,
                    account_name,
                    account_type,
                    order,
                    self.clock.now_iso(),
                    self.actor.user_id,
                ),
            )
            self._append_audit_event(
                event_type="ACCOUNT_CREATED",
                entity_type="ACCOUNT",
                entity_id=account_id,
                company_id=engagement["company_id"],
                engagement_id=engagement_id,
                description=f"Account {account_code} ({account_name}) added to {engagement['label']}.",
                details={"account_code": account_code},
            )
        return account_id

    def record_value(
        self,
        *,
        engagement_id: str,
        account_id: str,
        amount_minor: int,
        correction_reason: Optional[str] = None,
    ) -> str:
        """Append a financial-data revision (ADR-008: never update in place)."""
        financial_data_id = new_id()
        with self.transaction():
            engagement = self._load_open_engagement(engagement_id)
            account = self.connection.execute(
                "SELECT * FROM account WHERE account_id = ? AND engagement_id = ?",
                (account_id, engagement_id),
            ).fetchone()
            if account is None:
                raise ValidationError("The account does not belong to this engagement.")
            previous = self.connection.execute(
                "SELECT financial_data_id, revision_no, amount_minor FROM financial_data "
                "WHERE engagement_id = ? AND account_id = ? ORDER BY revision_no DESC LIMIT 1",
                (engagement_id, account_id),
            ).fetchone()
            revision_no = (previous["revision_no"] + 1) if previous else 1
            supersedes_id = previous["financial_data_id"] if previous else None
            if previous and revision_no > 1 and not correction_reason:
                correction_reason = "Draft correction"
            self.connection.execute(
                "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, "
                "amount_minor, currency_code, supersedes_id, correction_reason, recorded_at_utc, recorded_by) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    financial_data_id,
                    engagement_id,
                    account_id,
                    revision_no,
                    int(amount_minor),
                    engagement["currency_code"],
                    supersedes_id,
                    correction_reason,
                    self.clock.now_iso(),
                    self.actor.user_id,
                ),
            )
            event_type = "FINANCIAL_DATA_ADDED" if revision_no == 1 else "FINANCIAL_DATA_CHANGED"
            description = (
                f"Value recorded for {account['account_code']} in {engagement['label']} (revision {revision_no})."
                if revision_no == 1
                else f"Value corrected for {account['account_code']} in {engagement['label']} (revision {revision_no})."
            )
            self._append_audit_event(
                event_type=event_type,
                entity_type="FINANCIAL_DATA",
                entity_id=financial_data_id,
                company_id=engagement["company_id"],
                engagement_id=engagement_id,
                description=description,
                details={
                    "account_code": account["account_code"],
                    "revision_no": revision_no,
                    "supersedes_id": supersedes_id,
                },
            )
        return financial_data_id

    def latest_values(self, engagement_id: str) -> list[sqlite3.Row]:
        return list(self.connection.execute(_load_sql("latest_financial_values.sql"), {"engagement_id": engagement_id}))

    def value_history(self, engagement_id: str, account_id: str) -> list[sqlite3.Row]:
        return list(
            self.connection.execute(
                "SELECT * FROM financial_data WHERE engagement_id = ? AND account_id = ? ORDER BY revision_no",
                (engagement_id, account_id),
            )
        )

    # -- comparison ---------------------------------------------------------

    def comparative_rows(self, current_engagement_id: str) -> list[ComparativeRow]:
        current = self.get_engagement(current_engagement_id)
        prior_id = current["prior_engagement_id"]
        rows = self.connection.execute(
            _load_sql("comparative_values.sql"),
            {"current_engagement_id": current_engagement_id, "prior_engagement_id": prior_id},
        ).fetchall()
        return [
            ComparativeRow(
                account_code=row["account_code"],
                account_name=row["account_name"],
                prior_amount_minor=row["prior_amount_minor"],
                current_amount_minor=row["current_amount_minor"],
                prior_revision_no=row["prior_revision_no"],
                current_revision_no=row["current_revision_no"],
                is_new_account=bool(row["is_new_account"]),
                is_missing_in_current=bool(row["is_missing_in_current"]),
            )
            for row in rows
        ]

    # -- finalization -------------------------------------------------------

    def finalization_preflight(self, engagement_id: str) -> list[str]:
        """Return a list of blocking problems; empty means the engagement may be finalized."""
        problems: list[str] = []
        engagement = self.get_engagement(engagement_id)
        if engagement["status"] == "FINALIZED":
            problems.append("The engagement is already finalized.")
            return problems
        accounts = self.latest_values(engagement_id)
        if not accounts:
            problems.append("The engagement has no accounts.")
        missing = [a["account_code"] for a in accounts if a["amount_minor"] is None]
        if missing:
            problems.append("These accounts have no recorded value: " + ", ".join(sorted(missing)) + ".")
        orphan = self.connection.execute(
            "SELECT COUNT(*) AS c FROM financial_data f JOIN account a ON a.account_id = f.account_id "
            "WHERE f.engagement_id = ? AND a.engagement_id <> f.engagement_id",
            (engagement_id,),
        ).fetchone()["c"]
        if orphan:
            problems.append("Financial values reference accounts from another engagement.")
        if engagement["prior_engagement_id"]:
            prior = self.get_engagement(engagement["prior_engagement_id"])
            if prior["status"] != "FINALIZED":
                problems.append("The linked prior engagement is no longer finalized.")
        integrity = self.connection.execute("PRAGMA foreign_key_check").fetchall()
        if integrity:
            problems.append("Database foreign-key check reported violations.")
        return problems

    def build_manifest(self, engagement_id: str) -> tuple[str, str, int]:
        """Return (canonical_document, root_digest, account_count) for an engagement."""
        engagement = self.get_engagement(engagement_id)
        company = self.get_company(engagement["company_id"])
        prior_digest = None
        if engagement["prior_engagement_id"]:
            prior_digest = self.get_engagement(engagement["prior_engagement_id"])["finalization_digest"]
        accounts = [
            {
                "account_code": row["account_code"],
                "account_name": row["account_name"],
                "account_type": row["account_type"],
                "revision_no": row["revision_no"],
                "amount_minor": row["amount_minor"],
            }
            for row in self.connection.execute(_load_sql("finalization_scope.sql"), {"engagement_id": engagement_id})
        ]
        document = build_manifest_document(
            engagement_id=engagement["engagement_id"],
            company_id=company["company_id"],
            company_legal_name=company["legal_name"],
            company_short_name=company["short_name"],
            financial_year_id=engagement["financial_year_id"],
            financial_year_label=engagement["label"],
            period_start=engagement["period_start"],
            period_end=engagement["period_end"],
            currency_code=engagement["currency_code"],
            minor_unit_scale=engagement["minor_unit_scale"],
            prior_engagement_id=engagement["prior_engagement_id"],
            prior_root_digest=prior_digest,
            accounts=accounts,
        )
        return document, sha256_hex(document), len(accounts)

    def finalize_engagement(self, engagement_id: str, confirmation_text: Optional[str] = None,
                            expected_row_version: Optional[int] = None) -> str:
        """Finalize one engagement atomically; returns the root digest."""
        engagement = self.get_engagement(engagement_id)
        expected_confirmation = f"{engagement['company_short_name']} {engagement['label']}"
        if confirmation_text is not None and confirmation_text.strip() != expected_confirmation:
            raise ValidationError(f"Type '{expected_confirmation}' exactly to confirm finalization.")

        with self.transaction(immediate=True):
            engagement = self._load_open_engagement(engagement_id, expected_row_version)
            problems = self.finalization_preflight(engagement_id)
            if problems:
                raise ValidationError("Finalization preflight failed: " + " ".join(problems))

            document, digest, account_count = self.build_manifest(engagement_id)
            manifest_id = new_id()
            now = self.clock.now_iso()
            self.connection.execute(
                "INSERT INTO finalization_manifest (manifest_id, engagement_id, manifest_version, root_digest, "
                "canonical_content, record_count, created_at_utc, created_by) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                (manifest_id, engagement_id, MANIFEST_VERSION, digest, document, account_count, now, self.actor.user_id),
            )
            self.connection.execute(
                "UPDATE engagement SET status = 'FINALIZED', finalized_at_utc = ?, finalized_by = ?, "
                "finalization_digest = ?, finalization_manifest_version = ?, row_version = row_version + 1 "
                "WHERE engagement_id = ?",
                (now, self.actor.user_id, digest, MANIFEST_VERSION, engagement_id),
            )
            self._append_audit_event(
                event_type="ENGAGEMENT_FINALIZED",
                entity_type="ENGAGEMENT",
                entity_id=engagement_id,
                company_id=engagement["company_id"],
                engagement_id=engagement_id,
                description=(
                    f"{engagement['label']} finalized for {engagement['company_legal_name']}; "
                    f"the financial year is now read-only."
                ),
                details={"root_digest": digest, "manifest_version": MANIFEST_VERSION, "account_count": account_count},
            )
        return digest

    def verify_finalization_digest(self, engagement_id: str) -> bool:
        engagement = self.get_engagement(engagement_id)
        if engagement["status"] != "FINALIZED":
            return False
        stored = self.connection.execute(
            "SELECT canonical_content, root_digest FROM finalization_manifest WHERE engagement_id = ?",
            (engagement_id,),
        ).fetchone()
        if stored is None:
            return False
        recomputed_document, recomputed_digest, _ = self.build_manifest(engagement_id)
        return (
            stored["root_digest"] == engagement["finalization_digest"]
            and sha256_hex(stored["canonical_content"]) == stored["root_digest"]
            and recomputed_document == stored["canonical_content"]
            and recomputed_digest == stored["root_digest"]
        )

    # -- audit trail queries -------------------------------------------------

    def audit_events(self, *, engagement_id: Optional[str] = None, company_id: Optional[str] = None,
                     limit: int = 200) -> list[sqlite3.Row]:
        sql = "SELECT * FROM audit_event WHERE 1 = 1"
        params: list[object] = []
        if engagement_id:
            sql += " AND engagement_id = ?"
            params.append(engagement_id)
        if company_id:
            sql += " AND company_id = ?"
            params.append(company_id)
        sql += " ORDER BY sequence_no DESC LIMIT ?"
        params.append(limit)
        return list(self.connection.execute(sql, params))

    # -- backup --------------------------------------------------------------

    def create_backup(self, destination_dir: Path) -> Path:
        """Create a transactionally consistent backup package (no admin rights required)."""
        destination_dir = Path(destination_dir)
        destination_dir.mkdir(parents=True, exist_ok=True)
        stamp = self.clock.now_iso().replace(":", "").replace("-", "").replace(".", "")
        package_dir = destination_dir / f"audit-workbench-backup-{stamp}"
        staging_dir = destination_dir / f".{package_dir.name}.partial"
        if staging_dir.exists():
            raise WorkspaceError("A previous backup attempt left staging files; remove them and retry.")
        staging_dir.mkdir(parents=True)

        db_copy = staging_dir / "workspace.db"
        target = sqlite3.connect(str(db_copy))
        try:
            with target:
                self.connection.backup(target)  # SQLite online backup API
        finally:
            target.close()

        checksum = hashlib.sha256(db_copy.read_bytes()).hexdigest()
        manifest = {
            "package_format": "AWB-BACKUP/1.0",
            "created_at_utc": self.clock.now_iso(),
            "created_by": self.actor.username,
            "schema_version": SCHEMA_VERSION,
            "workspace_format_version": WORKSPACE_FORMAT_VERSION,
            "files": [{"name": "workspace.db", "sha256": checksum, "bytes": db_copy.stat().st_size}],
            "engagement_digests": [
                {"engagement_id": row["engagement_id"], "root_digest": row["finalization_digest"]}
                for row in self.connection.execute(
                    "SELECT engagement_id, finalization_digest FROM engagement WHERE status = 'FINALIZED' "
                    "ORDER BY engagement_id"
                )
            ],
        }
        (staging_dir / "backup-manifest.json").write_text(
            json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8"
        )
        os.replace(staging_dir, package_dir)  # atomic activation

        with self.transaction():
            self._append_audit_event(
                event_type="BACKUP_CREATED",
                entity_type="WORKSPACE",
                entity_id=package_dir.name,
                description=f"Backup package '{package_dir.name}' created.",
                details={"file_count": 2, "destination_class": "USER_SELECTED_FOLDER"},
            )
        return package_dir

    @staticmethod
    def verify_backup(package_dir: Path) -> list[str]:
        """Validate a backup package; returns a list of problems (empty = valid)."""
        package_dir = Path(package_dir)
        problems: list[str] = []
        manifest_path = package_dir / "backup-manifest.json"
        if not manifest_path.exists():
            return ["Backup manifest is missing."]
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("package_format") != "AWB-BACKUP/1.0":
            problems.append("Unsupported backup package format.")
        for entry in manifest.get("files", []):
            file_path = package_dir / entry["name"]
            if not file_path.exists():
                problems.append(f"Backup file '{entry['name']}' is missing.")
                continue
            if hashlib.sha256(file_path.read_bytes()).hexdigest() != entry["sha256"]:
                problems.append(f"Backup file '{entry['name']}' failed its checksum verification.")
        db_path = package_dir / "workspace.db"
        if db_path.exists():
            conn = sqlite3.connect(str(db_path))
            try:
                integrity = conn.execute("PRAGMA integrity_check").fetchone()[0]
                if integrity != "ok":
                    problems.append(f"Restored database integrity check failed: {integrity}")
                if conn.execute("PRAGMA foreign_key_check").fetchall():
                    problems.append("Restored database has foreign-key violations.")
            finally:
                conn.close()
        return problems

    # -- demo data -----------------------------------------------------------

    def seed_demo_data(self) -> dict:
        """Create the synthetic ABC Manufacturing (Demo) dataset. Synthetic data only (ADR-012)."""
        from demo_data import seed_demo_dataset  # local import keeps the dependency one-way

        return seed_demo_dataset(self)


class _Transaction:
    """One application command = one database transaction (architecture.md section 7)."""

    def __init__(self, connection: sqlite3.Connection, immediate: bool = False) -> None:
        self.connection = connection
        self.immediate = immediate

    def __enter__(self) -> "_Transaction":
        self.connection.execute("BEGIN IMMEDIATE" if self.immediate else "BEGIN")
        return self

    def __exit__(self, exc_type, exc, tb) -> bool:
        if exc_type is None:
            self.connection.execute("COMMIT")
        else:
            self.connection.execute("ROLLBACK")
        return False


def _transaction_with_mode(self: Workspace, immediate: bool = False) -> _Transaction:
    return _Transaction(self.connection, immediate=immediate)


Workspace.transaction = _transaction_with_mode  # type: ignore[assignment]


def _require_iso_date(value: str, field: str) -> None:
    try:
        date.fromisoformat(value)
    except (TypeError, ValueError) as error:
        raise ValidationError(f"{field} must be a valid ISO date (YYYY-MM-DD).") from error


def translate_guard_error(error: sqlite3.Error) -> WorkspaceError:
    """Map a database guard abort to an application error with a safe message."""
    message = str(error)
    if GUARD_PREFIX in message:
        detail = message.split(": ", 1)[-1]
        if "FINALIZED" in message or "APPEND-ONLY" in message:
            return EngagementFinalizedError(detail)
        return WorkspaceError(detail)
    return WorkspaceError(message)


def _sql_literal(value: str) -> str:
    """Quote a trusted internal string for inclusion in a DDL script."""
    return "'" + str(value).replace("'", "''") + "'"
