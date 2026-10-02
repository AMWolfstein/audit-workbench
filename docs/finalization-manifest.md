# Finalization Manifest `AWB-MANIFEST/1.0`

This document specifies the canonical manifest document and root digest produced by the
finalization transaction (architecture.md section 8, data-model.md section 6). It is a
**contract**: the .NET application and the cross-runtime verification harness
(`tools/verification`) must produce byte-identical documents and digests for the same
engagement content.

## 1. Why a canonical document

The digest must depend only on business content, never on SQLite physical layout, row
order, journal state, or formatting choices. Hashing the `.db` file is explicitly rejected
because vacuuming or journaling changes bytes without changing business content.

## 2. Document grammar

The manifest is UTF-8 text without a byte-order mark. Lines are joined with a single
`\n` (U+000A); the document ends with a trailing `\n`.

```text
AWB-MANIFEST/1.0
engagement_id=<uuid>
company_id=<uuid>
company_legal_name=<escaped text>
company_short_name=<escaped text>
financial_year_id=<uuid>
financial_year_label=<escaped text>
period_start=<YYYY-MM-DD>
period_end=<YYYY-MM-DD>
currency_code=<AAA>
minor_unit_scale=<integer>
prior_engagement_id=<uuid|NONE>
prior_root_digest=<64 hex chars|NONE>
account_count=<integer>
value_count=<integer>
account|<code>|<escaped name>|<type>|<revision_no|NONE>|<amount_minor|NONE>
...
END
```

Rules:

1. Header lines appear exactly once, in the order above.
2. `account|` lines are sorted ascending by `account_code` using ordinal (byte) comparison.
3. `account_count` is the number of accounts owned by the engagement; `value_count` is the
   number of accounts that have at least one financial-data revision.
4. An account with no recorded value emits `NONE` for both revision and amount.
5. Amounts are the **latest revision** minor units, rendered as a plain signed integer.
6. `prior_engagement_id` / `prior_root_digest` record the linked finalized source, or
   `NONE` when the engagement has no prior-year relationship.

### Escaping

Inside escaped text fields the following characters are replaced, in this order:

| Character | Replacement |
|---|---|
| `\` (U+005C) | `\\` |
| `\|` (U+007C) | `\p` |
| LF (U+000A) | `\n` |
| CR (U+000D) | `\r` |

## 3. Root digest

```text
root_digest = lowercase_hex(SHA-256(utf8_bytes(manifest_document)))
```

The digest is stored in `finalization_manifest.root_digest` and copied to
`engagement.finalization_digest`. Database trigger
`trg_engagement_finalization_requires_manifest` refuses a `FINALIZED` transition unless a
manifest row with the identical digest already exists in the same transaction.

The digest provides **tamper evidence only**. It is not a digital signature and does not
prevent a filesystem owner from rewriting both data and digest (ADR-007 residual risk).

## 4. Conformance fixture

`tests/fixtures/manifest_v1_example.txt` contains a frozen example document and
`tests/fixtures/manifest_v1_example.sha256` its expected digest. Both the .NET test suite
(`AuditWorkbench.Domain.Tests/FinalizationManifestTests.cs`) and the Python harness
(`tools/verification/tests/test_manifest_contract.py`) assert against these files, so a
change in either implementation that breaks the contract fails the build.

## 5. Verification points

The digest is recomputed from live data and compared with the stored value:

- immediately after the finalization commit;
- whenever a finalized engagement is opened or compared;
- before a backup package is written; and
- after a restore, before the workspace is activated.

A mismatch is surfaced as an integrity warning and recorded in the audit trail. The
application never silently replaces a stored digest.
