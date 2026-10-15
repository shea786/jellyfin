# ADR-0011: Supported database targets

- **Status:** Accepted, with the orchestrator rulings at the end
- **Date:** 2026-10-12

## Context

- **Direction.** "Multiple databases" means a concrete, ordered list of targets. Each target is implemented and passes the full parity suite on its own engine. PostgreSQL comes first. A second non-SQLite provider follows soon, so that more than one implementation proves the abstraction from ADR-0001 and it is not shaped around Postgres.
- **Demand.** The MySQL backend request (features.jellyfin.org #315) has 664 votes and status "started". Jellyfin lead Joshua Boniface (2025-06): an external database is the next phase, and "PostgreSQL support is likely to come first". SQL Server: 1 vote, declined. CockroachDB: no requests. The underlying driver is SQLite's "database is locked" (jellyfin#15101, open, 230 comments).
- **Provider packages.** The repo is on EF Core 10.0.11 (`Directory.Packages.props:36-39`, plus `HealthChecks.EntityFrameworkCore` at `:45`).
  - PostgreSQL: `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 requires EF `[10.0.4, 11)`, which fits.
  - MySQL/MariaDB:
    - Upstream Pomelo is stuck at 9.0.0. It has no EF 10 release (PomeloFoundation #2007), and 9.x throws `MissingMethodException` on EF 10.
    - `Microting.EntityFrameworkCore.MySql` 10.0.12 is an MIT fork of Pomelo with monthly releases. It requires EF ≥ 10.0.12. It is a small single-company project, partly AI-assisted, and its MariaDB CI coverage is unverified.
    - Oracle `MySql.EntityFrameworkCore` 10.0.9 is GPL-2.0 with the Universal FOSS Exception. It pins EF exactly and makes no claim of MariaDB support.
  - SQL Server: `Microsoft.EntityFrameworkCore.SqlServer`, first-party, MIT, on the same version train as EF.
  - CockroachDB: only through Npgsql, unofficially; Npgsql declined the migration-lock fix (#6025). No `CREATE AGGREGATE` (cockroach#74775), no `CREATE DATABASE … TEMPLATE`, no `xmin`. Proprietary licence since 24.3 (licence key and telemetry).
- **Engine probe, 2026-10-12** (PostgreSQL 16.15, MySQL 8.4.11, MariaDB 11.4.13, SQL Server 2022 CU27, CockroachDB 26.3.2, SQLite 3.53.4), run against our model of 31 entities and 64 indexes. The table under Decision and the amendments to ADRs 0001-0010 summarise the results.
- **Effort compared with Postgres:** CockroachDB small to medium, MariaDB large, MySQL larger, SQL Server large.

## Options considered

- **Second non-SQLite provider:**
  - **A. MySQL/MariaDB.** The highest demand. It exercises what Postgres does not: non-transactional DDL, no partial indexes, an index-key byte budget that needs prefix indexes, and a harness without template databases. Risks: the package (above) and a prerequisite EF bump.
  - **B. SQL Server.** No package risk, and the most different model: Guid order, cascade paths, the OR-shaped filter, `nvarchar(max)` cannot be indexed, and a unique index rejects multiple NULLs. But its DDL is transactional, so it tests the migration safety net no better than Postgres does. It has 1 vote, was declined upstream, and Express is capped at 10 GB (2022) or 50 GB (2025).
  - **C. CockroachDB.** It is the smallest change from Postgres, which is exactly why it would validate little.
- **Tiers.** (a) Declare every target "Supported" at once. (b) Explicit tiers with promotion criteria.

## Decision

**Tiers (b), in this order:**

| Order | Target | Tier | Floor | Reason |
|---|---|---|---|---|
| 0 | SQLite | Supported (default) | bundled `e_sqlite3` | The existing default. Every PR leaves it fully working. |
| 1 | PostgreSQL | Supported | 16 | The package is ready, upstream named it first, its DDL is transactional, and ADRs 0003-0009 complete the design. |
| 2 | MySQL + MariaDB (one provider) | Experimental, then Supported | MySQL 8.4 LTS, MariaDB 11.4 LTS | The highest demand. Promoted only once the package question settles (below). |
| 3 | SQL Server | Experimental | 2022 | First-party, and the strongest model-level check of the abstraction, but there is no demand and upstream declined it. |
| – | CockroachDB | Rejected | – | Licence (CI use needs a legal read), Npgsql migration lock declined, no template databases, every transaction would need retry under SERIALIZABLE, no demand. Reconsider only if both the licence and demand change. |

**Second provider: A (MySQL/MariaDB).** It has the most user value, and it validates the parts of the abstraction that Postgres cannot: the non-transactional migration guard, prefix indexes and the leased-pool harness. Three measures keep the abstraction from being shaped by Postgres or by MySQL:

- The shared prerequisites (group C0 in `pr-plan.md`) are sized against the tightest limit of **all four** server engines. For example, SQL Server's 900-byte clustered key sets the PK string bounds (ADR-0009 L1).
- The model-conformance tests below run on the design-time model of every provider project.
- Groups C (MySQL/MariaDB) and D (SQL Server) both depend only on C0. If the MySQL package question blocks, SQL Server goes second with no rework.

**What the tiers mean:**

- **Supported:** selectable, documented, and its CI job is a required check.
- **Experimental:** selectable, with a startup Warning that it is experimental. Its CI job runs on every PR but is not required. Known gaps are allowed if they have IDs. It may be withdrawn.
- **Rejected:** we do not build it. Third parties keep the `PLUGIN_PROVIDER` route.

**Promotion from Experimental to Supported needs all of:**

1. The parity suite is green, with no known gap except those this ADR accepts.
2. The migration safety-net test is green (ADR-0003 S1).
3. The benchmark (ADR-0005 R1) is within 3x of SQLite.
4. For MySQL/MariaDB, the package question is settled: Pomelo ships an EF 10 release; or Microting has shipped at least 6 consecutive monthly releases that track EF patches, with MariaDB in its CI; or the user accepts Oracle's package, which is MySQL-only and drops MariaDB.

**Packages, fallbacks, images and licences.** Client libraries are referenced only by their provider project, so swapping the MySQL package touches one project.

| Target | Package | Fallback | Test image (pin the tag here and the digest in CI; Renovate updates both) | Licence notes |
|---|---|---|---|---|
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 | none needed | `postgres:16.15-trixie`; nightly: newest 18.x `-trixie` | Npgsql and the server: PostgreSQL Licence |
| MySQL/MariaDB | `Microting.EntityFrameworkCore.MySql` 10.0.12, with the MySqlConnector driver (MIT) | (1) Pomelo, once it ships EF 10 (same code lineage, cheap swap). (2) Oracle `MySql.EntityFrameworkCore`: MySQL only, exact EF pin, conventions redone | `mysql:8.4.11`, `mariadb:11.4.13`; nightly: newest LTS of each | The forks are MIT. Oracle: GPL-2.0 + FOSS Exception. Both servers are GPL-2.0. |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer`, same version as EF | none needed | `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` with `MSSQL_PID=Developer`; nightly: newest 2025 CU | Provider: MIT. Developer edition is free for dev/test only, which is fine for CI. Production users need Express (10 GB cap on 2022, 50 GB on 2025) or a paid edition. |

- Test containers use `Testcontainers.{PostgreSql,MySql,MariaDb,MsSql}` 4.16.0. The modules' default images are stale, so always pass the pinned image, and confirm each exact tag string when its backend lands. Every client library is GPL-2.0-compatible.

**MySQL and MariaDB are one provider.**

- Key `Jellyfin-MySQL`. The server type comes from the `Options` entry `server-version` (for example `8.4-mysql` or `11.4-mariadb`), with auto-detection as the fallback.
- Migrations are generated once, against MySQL 8.4, and must apply on both servers.
- The test matrix has two entries, `MySql` and `MariaDb`.
- The provider branches on server type only for collation (ADR-0006 C2).

**Each parity mechanism, shared or per provider** (details in the amendments named in the third column):

| Mechanism | Where it lives | ADR |
|---|---|---|
| NULL ordering | Postgres provider only. The other engines already sort NULL lowest and reject `NULLS FIRST`. | 0006 C1 |
| Guid `MIN` and order | Per provider, through Guid storage. Postgres: the `min(uuid)` aggregate in its baseline. MySQL, MariaDB and SQL Server: `binary(16)` in canonical byte order. | 0007 G1-G2 |
| Collation | Per-provider convention | 0006 C2-C3 |
| Case-insensitive matching | Shared: rewrite D now, normalised columns (E) before the second provider | 0005 E1-E5 |
| Length limits | Shared bounds for key-class strings; per-provider storage for text-class strings | 0009 L1-L4 |
| UTC and precision | Shared converter and tolerant comparison; per-provider column type and sentinels | 0004 T1-T3 |
| Retry classification | Shared helper, per-provider classifier | 0008 R2-R4 |
| Identity reseed and explicit-ID insert | Shared restore flow, per-provider members | 0003 S2 |
| Migration safety net | Shared guard behind a capability flag, per-provider transactional settings | 0003 S1 |
| Filtered indexes, identifier length, cascade paths | Per-provider conventions; one shared convention class for identifier length | 0001 M3-M6 |

## Consequences

- **SQLite.** The whole plan has exactly one SQLite migration: option E (`pr-plan.md` PR 20). Everything else is SQLite-neutral. Verified: with EF 10.0.11, a change that only adds `MaxLength` produces **zero** SQLite migration operations.
- **EF bump.** The bump to 10.0.12 is a precondition of group C. It is offered upstream as an independent PR.
- **Plugin route.** Each provider project depends only on `Jellyfin.Database.Implementations`. It can therefore ship in-tree (registered in `DatabaseProviderTypes()`) or as a `PLUGIN_PROVIDER` assembly, with no code change. The design does not depend on the upstream route.
- **CI.** It grows to four server jobs. SQL Server images are amd64-only and need 2 GB of RAM, so developers on ARM hosts use an external server.
- **Accepted divergence:** ADR-0005 E2 declares non-ASCII case folding in the activity-log filters engine-defined.
- **SQL Server known gaps (Experimental):** trailing-space equality and UTF-16 order (ADR-0006 C3), and index keys over 1700 bytes (ADR-0009 L3).

## Tests required (definition of done, per target)

1. The whole fixture-based suite is green with `JELLYFIN_TEST_DB_PROVIDER=<engine>`. Any known gap carries its ID.
2. ADR-0002 migration tests (a)-(c) pass for the provider, including the same-ID parity check.
3. **Model conformance**, with no server needed:
   - every identifier is within the engine's limit and unique per schema (ADR-0001 M4);
   - no index exceeds the engine's key budget (ADR-0009 L1);
   - no filter uses syntax the engine rejects;
   - every string in a key or index is either key-class or text-class;
   - SQL Server only: no cascade cycle.
4. Provider member tests: purge, then the first identity value is 1; the backup sentinel; the retry classifier codes; a restore round trip followed by an insert (ADR-0003 S2).
5. The migration safety-net test passes (ADR-0003 S1).
6. The CI sentinel `DatabaseBackend_IsLive` shows a server version at or above the floor (ADR-0010 CI2).
7. The benchmark results (ADR-0005 R1) are recorded in the PR explainer.

## Upstream context (for the user; this does not change the design)

- **JPVenson's plugin.** JPVenson, the core developer behind Jellyfin's EF Core migration, maintains an out-of-tree plugin, `JPVenson/Jellyfin.Pgsql` (GPL-3.0, about 160 stars, built for 10.11). Maintainers may prefer that route, with providers as `PLUGIN_PROVIDER` plugins, over in-tree providers.
- **How the upstream Meta discussion might be framed:**
  - Groups 0, A and C0 harden the abstraction whichever route is chosen.
  - The provider projects work with either route.
  - Ask maintainers, per engine, whether they want it in-tree or as a plugin, and whether to coordinate with JPVenson.
- **Do not copy or adapt that plugin's code.**
  - The plugin is GPL-3.0, and Jellyfin's root `LICENSE` is GPL-2.0.
  - Note: several Jellyfin packages, including `Jellyfin.Database.Implementations`, declare `GPL-3.0-only` in their csproj (`PackageLicenseExpression`). Licensing that is mixed in this way is a question for the maintainers. It does not change our rule.
  - Implementers do not read its source. Any comparison cites behaviour only.
  - The same rule applies to the community MariaDB plugin by canepan, whose licence we have not checked.

## Orchestrator rulings (2026-10-12)

- **R1. MySQL/MariaDB package: build on `Microting.EntityFrameworkCore.MySql`, but do not offer it upstream until the package question is settled.**
  - Microting keeps Pomelo's API lineage, so code written against it moves to Pomelo with little more than a package swap once Pomelo ships EF 10. It also keeps MariaDB, which Oracle drops.
  - Group C is built and tested in the fork against an exact pinned version. Before the package is added, a supply-chain review checks: package signing and ownership, that the source repository matches the published package, the licence, and the release history.
  - Group C stays **fork-only and Experimental**, and is not proposed upstream, until promotion criterion 4 holds. A dependency on a small, single-company, partly AI-assisted fork is the first thing upstream reviewers would challenge, and the user should not have to defend it.
  - The EF Core bump to 10.0.12 (PR 23) has value of its own and can go upstream independently at any time.
- **R2. Provider order: PostgreSQL, then MySQL/MariaDB, then SQL Server.**
  - MySQL/MariaDB goes second because it validates what Postgres cannot: non-transactional DDL, prefix indexes and the leased-pool harness. The package risk sits in one project, so it does not slow the design work.
  - If the package review in R1 fails, SQL Server moves to second place without rework, because groups C and D both depend only on C0.
  - **SQL Server stays fork-only and Experimental.** Upstream declined it and there is no demand. It is offered upstream only if maintainers ask for it.
- **R3. Deliberate SQLite behaviour changes are accepted, and none of them may surprise a reviewer.** Each one lands in its own PR, with a test that pins the new behaviour and a section headed "Behaviour change on SQLite" in that PR's explainer. The commit body states it in one sentence. The changes:
  - Key lengths (ADR-0009 L2a-L2e as amended, PR 19): a user data key or `DeviceId` longer than its bound is stored under a prefix-and-hash form instead, and stored values are rewritten once. A username, display preferences client or preference key over its bound gets a 400. A provider id over 128 characters is dropped with a warning. Before, SQLite stored all of them as given. The explainer lists every bound, where its values come from, and which rule applies.
  - Ingest truncation (ADR-0009 L2, PR 19): a person `Role` longer than 400 characters, or an `ItemValues.Value` longer than 512, is truncated before de-duplication.
  - Unicode case folding (ADR-0005 E4, PR 20): `émile` finds `Émile`, OriginalTitle search ignores diacritics, and `/Persons` collapses `Émile`/`émile`. The explainer presents this as fixing the known duplicate-person bug.
  - The `NameContains` → `CleanName` fix (ADR-0005, P5) gets the same treatment.
- **R4. ADR-0002 H1 is amended:** the backend's `Create` stays synchronous. See the ADR-0002 amendments.
- **Left to the user, and covered in the explainers rather than decided here:**
  - whether to raise the GPL-2.0 `LICENSE` vs `GPL-3.0-only` package-metadata mismatch with maintainers;
  - in-tree providers or the plugin route, which is the Meta discussion.

  Our design works either way, and the rule against copying from the GPL-3.0 plugin applies regardless.

## Amendments (step 24 spike, 2026-10-13)

- **F1. MariaDB floor 11.4.7, MySQL floor 8.4.** See ADR-0006 C4. The provider checks the server version when it initialises and refuses an older one with a message naming the floor. CI pins `mysql:8.4.11` and `mariadb:11.4.13`.
- **F2. Lists of ids.** Microting 10.0.12 cannot send a list as one parameter on either server: the JSON path fails with "no type mapping", and the package has switches to turn it off because JSON_TABLE over a parameter can crash MySQL 8. The provider uses `UseParameterizedCollectionMode(MultipleParameters)`, does not call `EnablePrimitiveCollectionsSupport` (calling it afterwards silently resets the mode), and rewrites `EF.Parameter` lists to `EF.MultipleParameters` with a query expression interceptor. Interceptors are single shared instances, so EF does not build a service provider per context.
  - EF pads the count into buckets, so the number of distinct statements stays small.
  - The 65,535-placeholder limit applies only to server-side prepared statements, which EF does not use, so 70,000 and 200,000 ids run (241 / 212 ms and 547 / 607 ms, MySQL / MariaDB).
  - The real limit is `max_allowed_packet`, at about 26 bytes per id. MariaDB's default 16 MiB takes 600,000 ids and fails at 1,000,000; MySQL's 64 MiB takes 1,000,000. The largest lists Jellyfin builds are whole-library descendant sets (item counts, library delete). The how-to recommends `max_allowed_packet` of at least 64M on MariaDB, and the ledger records lists past the packet size as a known gap. A temporary-table join would need a shared hook and is not planned.
- **F3. Identity restart and restore.** On both servers `DELETE` keeps the counter and `ALTER TABLE ... AUTO_INCREMENT = 1` after it restarts it at 1, also for foreign-key children and across restarts. InnoDB keeps the counter above the highest explicit id by itself, persistently. So the provider's purge deletes inside one transaction with foreign-key checks off, then resets the counter only on tables where it is above 1 (that is the cheap case for a test lease that touched few tables). The restore hooks stay empty. `NO_AUTO_VALUE_ON_ZERO` stays out of `sql_mode`.

## Amendments (step 24 gate, 2026-10-14)

- **F4. Identity reset, as built (supersedes the purge part of F3).** The purge deletes rows with `foreign_key_checks` off, inside one transaction. It then runs `ALTER TABLE ... AUTO_INCREMENT = 1` on every table whose column metadata (`information_schema.COLUMNS`) marks an `auto_increment` column, not only on tables whose counter is above 1. In InnoDB that statement changes metadata only, so it is cheap.
  - **Inside a transaction the caller holds** (the restore path), the purge deletes rows only. An `ALTER` would commit that transaction implicitly. The restore then inserts rows with their own ids, and InnoDB moves each counter past the highest of them. So the test after a restore checks that new rows get ids that collide with none, not exact ids.
- **F5. No fast backup.** `MigrationBackupFast` returns the empty key: there is nothing to snapshot on the server. It warns that a dump made with `mysqldump` or `mariadb-dump` is the only way back from a failed upgrade. `RestoreBackupFast` with the empty key logs, at Critical, the last migration that applied completely and returns without throwing.
- **F6. Server version and connection.** The provider reads the server from the `server-version` option when it is set, and otherwise from the version the server reports on connecting. A reported version it cannot read is refused, with a message that names the option, unless the option is set (a proxy or a fork may report a version of its own). The `5.5.5-` prefix that MariaDB reports through the replication protocol is read correctly. The connection character set is forced to `utf8mb4`, because the column collation is invalid under `utf8mb3` (error 1253); a different setting in the connection string is logged as a Warning and replaced.

## Amendments (step 24b, 2026-10-14)

- **F7. `Max` and `Min` over a union on MariaDB.** EF Core translates `Max` or `Min` over `Concat` into a scalar subquery over a `UNION ALL` derived table. When the union reads a column of the outer query, as the `DatePlayed` sort does, MariaDB cannot resolve it and fails ("Unknown column ... in 'WHERE'"). No shared query shape passed the step 24b merge bar: sixteen variants stayed one correlated subquery per row, and a pre-aggregated join took the folder and search cases from under 10 ms to 47 to 276 ms, with no filter pushdown on SQLite or PostgreSQL. So the shared query is unchanged, and the provider registers a query expression interceptor for MariaDB only. It rewrites `Max` or `Min` of two concatenated sequences, for a nullable number or `DateTime` result, into the null-safe greater or lesser of the two aggregates, each a plain scalar subquery. Other shapes and other servers keep the original query; SQLite, PostgreSQL and MySQL 8.4 send the same SQL as before. The gap `MARIADB-OUTER-REF-DERIVED` is removed. Nested unions are rewritten recursively and grow the query by about three copies per level; the only caller has one union.

## Amendments (step 26, 2026-10-15)

- **F8. MySQL-family CI as built.**
  - **Every pull request.** The MySQL family's CI job now runs on every pull request, as the Experimental tier says, and also nightly and on demand. It is not a required check (`.github/workflows/ci-tests-db-mysql.yml`, step 26, pr-plan B30).
  - **Required-tier entries.** The images of the table above, `mysql:8.4.11` and `mariadb:11.4.13`, pinned by digest.
  - **Newest LTS.** The "newest LTS of each" in the table is now two advisory entries on every run, not only nightly. Their failure does not fail the run:
    - **MySQL 9.7.2**, the newest LTS line; 26.7 is an Innovation release and is not tested. Release policy: <https://dev.mysql.com/doc/refman/26.7/en/mysql-releases.html>, <https://blogs.oracle.com/mysql/mysql-9-7-0-lts-is-now-available-expanded-community-capabilities-and-dynamic-data-masking-for-enterprise>.
    - **MariaDB 12.3.3**, a long-term release maintained until June 2029. Release policy: <https://mariadb.org/mariadb-server-12-3-lts-released/>, <https://mariadb.com/docs/release-notes/community-server/12.3/12.3.1>.
  - **Known failure.** MariaDB 11.8 and later can fail with error 1020 until 27a, because MySqlConnector sends REPEATABLE READ for a transaction without a level (pr-plan B30). That also needs a correction to ADR-0008 R3, which 27a records.
