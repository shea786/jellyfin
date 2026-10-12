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
| [0004](0004-utc-datetime-handling.md) | UTC date/time storage, precision and sentinel values | Accepted |
| [0005](0005-case-insensitive-matching.md) | Case-insensitive matching and LIKE semantics | Accepted |
| [0006](0006-string-collation-and-null-ordering.md) | String collation and deterministic ordering, including NULLs | Accepted |
| [0007](0007-guid-aggregates-and-unsupported-translations.md) | Aggregates over Guid and other provider-sensitive translations | Accepted |
| [0008](0008-concurrency-locking-and-retry.md) | Concurrency tokens, locking strategy and retry policy | Accepted |
| [0009](0009-column-length-and-text-sanitisation.md) | Column length limits and text sanitisation | Accepted |
| [0010](0010-ci-gating-for-database-provider-tests.md) | CI gating for database provider tests | Accepted |

Supporting document: [pr-plan.md](pr-plan.md), the ordered, stacked upstream PR plan.
