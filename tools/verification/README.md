# SQL verification harness

A small, dependency-free harness that executes the **real** shipped SQL — the migrations in
`db/migrations/` and the queries in `db/sql/` — against SQLite and asserts the MVP invariants.

It is a development and review tool. It is not part of the distributable package, it imports
nothing outside the Python standard library, it needs no network access, and it writes only to a
temporary directory that it deletes afterwards.

## Run it

```bash
python3 tools/verification/run_verification.py        # run every check
python3 tools/verification/run_verification.py -v     # verbose, one line per test
python3 tools/verification/run_verification.py --demo # print the documented demo scenario
python3 tools/verification/client_package.py CLIENT.awb # independently verify AWB-CLIENT/1.0
```

Expected output of a healthy run:

```
Ran 109 tests in ...s

OK
```

`--demo` seeds the synthetic ABC Manufacturing dataset in a throwaway workspace and prints the
comparative table, the finalization digest check and the audit-chain check:

```
Account                           FY2026              FY2027          Difference    % Change
--------------------------------------------------------------------------------------------
Revenue                   850,000,000.00      920,000,000.00       70,000,000.00        8.24
Trade receivables         180,000,000.00      210,000,000.00       30,000,000.00       16.67
Inventory                 240,000,000.00      275,000,000.00       35,000,000.00       14.58

FY2026 digest verifies : True
Audit hash chain valid : True
Audit events recorded  : 18
```

## Why it exists

The guarantees of this MVP live in the database: a finalized financial year is read-only because
`STRICT` tables, `CHECK` constraints, composite foreign keys and `AWB-GUARD-*` triggers make the
forbidden states unrepresentable (ADR-018). Those rules are written once, in SQL, and are shared by
the .NET application and this harness. Running them from a second, independent runtime proves the
invariants hold in the schema itself rather than in one application's service layer, and it keeps
working on a machine with no .NET SDK installed.

The harness and the C# code are also held to one shared contract: the canonical finalization
manifest (ADR-017). `tests/fixtures/manifest_v1_example.txt` and `.sha256` are byte-frozen and
asserted from both sides, so the Python and C# digest implementations cannot drift apart.

## Layout

| Path | Contents |
|---|---|
| `workspace.py` | Opens a temporary workspace, applies the migrations with their checksum ledger, exposes transactions, the audit-event writer and the manifest/digest rules |
| `demo_data.py` | The synthetic ABC Manufacturing dataset, identical to the C# `DemoDataSeeder` |
| `run_verification.py` | Test entry point (`unittest` discovery) plus the `--demo` report |
| `tests/_harness.py` | Shared fixtures: fixed clock, seeded workspace, raw-SQL helpers |
| `tests/test_year_isolation.py` | Each year owns its data; current-year work never touches the prior year |
| `tests/test_finalization.py` | Finalization is atomic, audited and irreversible; guards reject direct SQL |
| `tests/test_comparative.py` | Comparative figures, including the documented 8.24 / 16.67 / 14.58 percentages |
| `tests/test_audit_trail.py` | Required event vocabulary, append-only enforcement, hash-chain verification |
| `tests/test_backup.py` | Backup package layout, checksum and restorability |
| `tests/test_schema_contract.py` | The manifest fixture and the schema/guard vocabulary |
| `tests/test_financial_data.py` | Migration 0007: financial periods, TB/GL imports, versioning, roll-forward comparison and their guards |

## Relationship to the .NET tests

| Layer | Project | Needs |
|---|---|---|
| Domain rules (money, status machine, manifest grammar, comparative maths) | `tests/AuditWorkbench.Domain.Tests` | .NET 8 SDK |
| Services against a real on-disk SQLite workspace | `tests/AuditWorkbench.Application.Tests` | .NET 8 SDK |
| Shipped SQL against SQLite | this harness | Python 3.9+ |

The .NET suites are the primary tests for the application; this harness is the one that can be run
anywhere, including in environments where the .NET toolchain is unavailable.
