# Architecture Decision Records — multi-database fork

These records hold the design decisions of this Jellyfin fork for first-class multi-database support: PostgreSQL first, then MySQL/MariaDB and SQL Server.

- The fork owns this design and does not wait for upstream. The work still goes upstream as a stack of small PRs, each independently mergeable, each leaving SQLite fully working (see [pr-plan.md](pr-plan.md)).
- Each ADR records the context, verified against code at `file:line`, the options weighed, the decision and its consequences. ADRs are only ever superseded, never edited after the fact.
- This folder belongs to the fork alone and is never included in upstream PRs.

| # | Title | Status |
|---|-------|--------|
| [0001](0001-fork-owned-multi-provider-architecture.md) | Fork-owned multi-provider architecture | Accepted |
| [0002](0002-cross-provider-test-infrastructure.md) | Cross-provider test infrastructure | Accepted |
| [0003](0003-postgres-provider-baseline-and-migrations.md) | Postgres provider baseline and migrations | Accepted |
| 0004 | UTC date/time handling | Pending (DB parity) |
| 0005 | Case-insensitive text and collation | Pending (DB parity) |
| 0006 | NULL ordering | Pending (DB parity) |
| 0007 | Guid MIN aggregates | Pending (DB parity) |
| 0008 | Concurrency tokens and locking | Pending (DB parity) |
| 0009 | String length limits | Pending (DB parity) |
| [0010](0010-ci-gating-for-database-provider-tests.md) | CI gating for database provider tests | Accepted |

The titles of 0004-0009 are provisional until the DB-parity ADRs are written.

Supporting document: [pr-plan.md](pr-plan.md), the ordered, stacked upstream PR plan.
