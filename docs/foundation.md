# Foundation contract

This document records the implemented foundation boundary. Advanced audit execution modules must build on these controls rather than bypass them.

## Identity and authorization

`ICurrentActor` is the only source of request identity. The local SQLite host uses the clearly marked `LocalActor` for development. A shared/server host sets `Workbench:IdentityMode=Claims` and configures an approved ASP.NET Core authentication handler; `HttpCurrentActor` then resolves the stable application-user UUID from the authenticated `NameIdentifier`/`sub` claim. Route, form, query and command user IDs never select the actor.

Engagement authorization is deny-by-default:

```text
active User -> active EngagementMember -> Role -> RolePermission -> action
```

`EngagementAuthorizationService.RequireAsync` is called by engagement commands and queries. Unknown and unauthorized engagement IDs produce the same forbidden result. Client metadata is also filtered: `CompanyAuthorizationService` permits access through an authorized engagement. The creator may access a newly created client only until its first engagement exists, allowing safe bootstrap without creating a permanent client-wide bypass. Creating another period requires client management authority. Nested IDs (account, assignment, prior engagement) are checked against their engagement.

`VIEW_AUDIT_TRAIL` controls engagement audit reads. Unscoped audit queries return only permitted engagements; workspace events require workspace privilege. Audit-chain verification also requires audit/workspace authority.

## Data ownership and history

```text
Company (client)
  -> Engagement (one audit file / year boundary)
     -> FinancialYear (immutable dates)
     -> year-owned audit records
```

A unique `(company_id, financial_year_id)` constraint prevents duplicate years, overlap is rejected, and every year-owned row carries `engagement_id`. A prior-year relationship is an immutable reference to an earlier finalized engagement of the same client. It does not copy or edit the prior year. Finalized engagement data, manifests, revisions and audit events have database write guards.

Users, memberships and assignments are deactivated or moved to terminal states rather than deleted. Membership role/status changes and assignment creation, reassignment and terminal changes are audit events. Migration `0006_foundation_integrity` makes membership identity and assignment ownership/scope immutable, verifies active assignee membership, and adds access indexes. Evidence and audit history are never hard-deleted by normal application services.

## Concurrency and errors

Mutable aggregates carry an integer `row_version` mapped as an EF Core concurrency token. Commands compare the version read by the caller and EF includes the original token in its update. Stale writes become `AWB-CONCURRENCY`; they are never retried as overwrite. Append-only financial values use expected revision numbers. Concurrent audit-sequence collisions are also reported as concurrency conflicts.

`UnitOfWork` translates SQLite, PostgreSQL and SQL Server constraint failures at the provider boundary into safe validation, concurrency or integrity errors. Razor Pages display only application-safe messages; raw database exceptions are not returned to operators.

## Audit trail

A successful mutation and its audit event commit in one transaction. Events contain UTC time, server-resolved actor, company/engagement scope, entity identity, action/outcome and metadata-only details. The trail is append-only in the database and hash chained. Authorization/finalized-write refusals are recorded best-effort after the refused transaction rolls back. Imported source events are preserved in a separate append-only archive.

## Providers

The domain and application are provider-neutral. `AddAuditDatabase` selects PostgreSQL (preferred central production provider), SQL Server, or SQLite; SQLite is rejected when options declare a production deployment. Native UUID mappings remain enabled for PostgreSQL/SQL Server, while SQLite UUID text conversion, PRAGMAs, migration scripts and guard translation are isolated in infrastructure. Shared read SQL uses provider-neutral ADO.NET readers and parameters.

The checked-in `db/migrations` scripts are the local SQLite migration stream and are applied only by `SqlMigrationRunner`; central deployments must use reviewed provider-native DDL preserving the same named constraints and guards. The application does not silently substitute SQLite for a requested central provider.

## Portable client package

`.awb` is the versioned `AWB-CLIENT/1.0` ZIP contract, not a copied database. Its canonical manifest declares format/schema requirements, file paths, counts, byte lengths and SHA-256 digests. Whitelisted entries carry client, periods, engagements, accounts/TB foundation data, revision data, prior links, finalization manifests, team/assignment history, principals and archived audit events. Validation enforces path/size/compression limits, reference closure, checksums, package version and finalized-manifest integrity before an atomic import. Existing clients are not merged or overwritten in version 1.0.

Future working-paper, evidence, TB/GL and audit-area entries must be added through a new backward-compatible package version and remain engagement-owned.
