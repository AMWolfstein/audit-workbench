"""Synthetic demo dataset: ABC Manufacturing (Demo).

ADR-012: development, demonstrations, tests and screenshots use generated,
visibly synthetic organisations and values only. Nothing in this file may ever
be derived from a real client file, even an "anonymised" one.

The .NET application seeds the identical dataset
(src/AuditWorkbench.Application/DemoData/DemoDataSeeder.cs); the two must stay
in step so screenshots, tests and the harness describe the same workspace.
"""

from __future__ import annotations

DEMO_COMPANY = {
    "legal_name": "ABC Manufacturing (Demo) Limited",
    "short_name": "ABC-DEMO",
    "industry": "Manufacturing",
    "country_code": "ZZ",  # reserved/user-assigned code: never a real jurisdiction
    "tax_reference": "DEMO-TAX-0000001",
}

DEMO_ACCOUNTS = [
    {"code": "1200", "name": "Trade receivables", "type": "ASSET", "order": 20},
    {"code": "1300", "name": "Inventory", "type": "ASSET", "order": 30},
    {"code": "4000", "name": "Revenue", "type": "INCOME", "order": 10},
]

DEMO_FY2026 = {
    "label": "FY2026",
    "period_start": "2026-01-01",
    "period_end": "2026-12-31",
    "values": {"4000": "850000000", "1200": "180000000", "1300": "240000000"},
}

DEMO_FY2027 = {
    "label": "FY2027",
    "period_start": "2027-01-01",
    "period_end": "2027-12-31",
    "values": {"4000": "920000000", "1200": "210000000", "1300": "275000000"},
}


def seed_demo_dataset(workspace, *, finalize_prior_year: bool = True) -> dict:
    """Create the demo company, finalized FY2026 and linked draft FY2027."""
    from workspace import parse_amount_to_minor, ValidationError

    existing = [c for c in workspace.list_companies() if c["short_name"] == DEMO_COMPANY["short_name"]]
    if existing:
        raise ValidationError(
            "The demo dataset already exists in this workspace. "
            "Create a new workspace if you want a clean demonstration."
        )

    company_id = workspace.create_company(**DEMO_COMPANY)

    fy2026_id = workspace.create_engagement(
        company_id=company_id,
        label=DEMO_FY2026["label"],
        period_start=DEMO_FY2026["period_start"],
        period_end=DEMO_FY2026["period_end"],
    )
    _seed_year(workspace, fy2026_id, DEMO_FY2026["values"])

    digest = None
    if finalize_prior_year:
        digest = workspace.finalize_engagement(fy2026_id)

    fy2027_id = workspace.create_engagement(
        company_id=company_id,
        label=DEMO_FY2027["label"],
        period_start=DEMO_FY2027["period_start"],
        period_end=DEMO_FY2027["period_end"],
        prior_engagement_id=fy2026_id if finalize_prior_year else None,
    )
    _seed_year(workspace, fy2027_id, DEMO_FY2027["values"])

    workspace.connection.execute("BEGIN")
    try:
        workspace._append_audit_event(
            event_type="DEMO_DATA_SEEDED",
            entity_type="COMPANY",
            entity_id=company_id,
            company_id=company_id,
            description="Synthetic demo dataset 'ABC Manufacturing (Demo)' seeded.",
            details={"fy2026": fy2026_id, "fy2027": fy2027_id, "synthetic": True},
        )
        workspace.connection.execute("COMMIT")
    except Exception:
        workspace.connection.execute("ROLLBACK")
        raise

    return {
        "company_id": company_id,
        "fy2026_engagement_id": fy2026_id,
        "fy2027_engagement_id": fy2027_id,
        "fy2026_digest": digest,
    }


def _seed_year(workspace, engagement_id: str, values: dict) -> None:
    from workspace import parse_amount_to_minor

    engagement = workspace.get_engagement(engagement_id)
    scale = engagement["minor_unit_scale"]
    for account in DEMO_ACCOUNTS:
        account_id = workspace.add_account(
            engagement_id=engagement_id,
            account_code=account["code"],
            account_name=account["name"],
            account_type=account["type"],
            display_order=account["order"],
        )
        workspace.record_value(
            engagement_id=engagement_id,
            account_id=account_id,
            amount_minor=parse_amount_to_minor(values[account["code"]], scale),
        )
