# Concurrency model

Audit Workbench uses optimistic concurrency, not long-lived/distributed locks.

## Contract

Mutable aggregate rows carry `row_version`. EF Core maps it as a concurrency token. A command receives the version the user read, compares it in the domain, and the database update includes the original token. A zero-row update becomes `ConcurrencyException` (`AWB-CONCURRENCY`) with a reload/reapply message. Silent last-writer-wins is forbidden.

Example: A reads engagement version 7; B commits version 8; A submits version 7 and is rejected. Commands changing independent rows can commit independently. Transaction boundaries remain one mutation plus its audit event.

`Engagement`, `Company`, `EngagementMember`, and `Assignment` have tokens. Append-only rows use uniqueness/revision chains instead. PostgreSQL may later map tokens to an explicit numeric column (preferred for portability) rather than expose `xmin`; SQL Server may use the same numeric contract or a `rowversion` mapping. Domain code must not know the provider.

Finalization wins over ordinary edits: lifecycle checks and database guards still reject year-owned writes after finalization, irrespective of token or user. Clients should return HTTP 409 for stale versions and 423/409-style domain responses for finalized data, with no automatic overwrite retry.

SQLite tests exercise explicit versions. Production load tests must additionally verify central-provider transaction isolation, concurrent audit-event append behavior, and retry policy. Transient infrastructure retries may repeat a transaction; business conflicts must never be retried as overwrite.
