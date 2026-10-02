# Audit Workbench

Architecture and product specification for a **local, portable audit-management and working-papers application**. This repository currently contains documentation only; no application or production code has been implemented.

> All names and values in these documents are synthetic examples. Development and test data must never contain real client information.

## Architectural direction

Audit Workbench will be a self-contained **.NET 8 LTS ASP.NET Core** application, serving a server-rendered **Razor Pages** interface on the loopback interface only and storing data in a local **SQLite** database. A packaged release will include the .NET runtime and be runnable from a normal user-writable folder without installation, administrator rights, a Windows service, Docker, SQL Server, cloud access, or an internet connection.

The central invariant is:

> A company is master data; each financial year is a separate engagement boundary. Finalizing an engagement makes all of its year-owned records immutable to normal application operations. A later year references or copies from the finalized prior engagement—it never repurposes or updates that engagement.

## MVP

The first MVP proves the year-isolation design only:

1. Create a company and a financial-year engagement.
2. enter a small set of financial values;
3. finalize and lock that engagement;
4. create the next engagement and explicitly link the finalized prior year;
5. enter independent current-year values and show comparisons; and
6. demonstrate, through UI behavior, database constraints, audit events, and tests, that current-year work cannot alter the prior year.

Trial-balance import, planning, risks, materiality, working papers, review, findings, sign-off workflows, and document exports are deliberately outside this MVP.

## System context

```mermaid
flowchart LR
    U[Audit professional] -->|browser on same device| W[Audit Workbench\nlocalhost UI + application]
    W -->|transactional access| DB[(SQLite workspace)]
    W -->|explicit operation| B[Encrypted corporate/user backup location]
    W -. optional future export .-> X[Excel / PDF / Word]
    Internet((Internet / cloud)):::off
    Internet -. not required .- W
    classDef off fill:#eee,stroke:#888,stroke-dasharray: 5 5
```

## Documentation

| Document | Purpose |
|---|---|
| [Requirements](docs/requirements.md) | Scope, actors, functional/non-functional requirements, and acceptance criteria |
| [Architecture](docs/architecture.md) | Recommended stack, component design, portability, alternatives, testing, and roadmap |
| [Data model](docs/data-model.md) | Entity model, engagement ownership, constraints, revisions, prior-year links, and schema outline |
| [Security model](docs/security-model.md) | Threats, authentication direction, authorization, integrity, backups, and operational controls |
| [Audit-year lifecycle](docs/audit-year-lifecycle.md) | State machine, finalization protocol, prior-year creation, and roll-forward rules |
| [MVP scope](docs/mvp-scope.md) | Included/excluded behavior, demo scenario, acceptance tests, and phased delivery plan |
| [Decisions](docs/decisions.md) | Architecture decision records, assumptions, and deferred decisions |

## Key diagrams

### Entity overview

```mermaid
erDiagram
    COMPANY ||--o{ ENGAGEMENT : has
    FINANCIAL_YEAR ||--o{ ENGAGEMENT : describes
    ENGAGEMENT ||--o{ ACCOUNT : owns
    ENGAGEMENT ||--o{ FINANCIAL_DATA : owns
    ACCOUNT ||--o{ FINANCIAL_DATA : classifies
    ENGAGEMENT ||--o| PRIOR_YEAR_RELATIONSHIP : current_year
    ENGAGEMENT ||--o{ PRIOR_YEAR_RELATIONSHIP : prior_year
    USER ||--o{ USER_ROLE : assigned
    ROLE ||--o{ USER_ROLE : grants
    USER ||--o{ AUDIT_EVENT : performs
    ENGAGEMENT ||--o{ AUDIT_EVENT : scopes
```

### Audit-year lifecycle

```mermaid
stateDiagram-v2
    [*] --> Draft: create engagement
    Draft --> Draft: enter/revise year data
    Draft --> Finalizing: request finalization
    Finalizing --> Draft: validation fails
    Finalizing --> Finalized: atomic validation + digest + lock
    Finalized --> [*]: retained read-only
    Finalized --> NextYearDraft: create independent engagement\nwith prior-year relationship
    NextYearDraft --> NextYearDraft: compare / selectively roll forward
```

Finalized is intentionally terminal. Exceptional reopening, if ever approved, will be a separately designed, privileged, fully audited process—not an ordinary status toggle.

## Recommended delivery phases

1. **Specification baseline (this commit):** agree invariants, acceptance criteria, schema concept, security boundaries, and decisions.
2. **Technical skeleton:** portable packaging proof, loopback-only host, SQLite migrations, health/startup handling, and no business UI beyond a diagnostic shell.
3. **Company and engagement core:** master data, year creation, explicit ownership, validation, and audit event infrastructure.
4. **Financial data and comparison:** append-only revisions, prior-year link, comparative query, and synthetic fixture.
5. **Finalization controls:** preflight, transaction, immutable-state database guards, digest/manifest, backup prompt, and negative tests.
6. **MVP hardening:** local identity/RBAC baseline, backup/restore validation, accessibility, failure recovery, packaging, and user acceptance testing.
7. **Post-MVP modules:** trial balance/mapping, materiality/risk/planning, procedures/working papers/review, findings/sign-off, controlled roll-forward, and exports.

No phase should begin by weakening the engagement boundary or treating “year” as a mutable field on one audit record.
