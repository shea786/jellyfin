# Explainer: step 14, PostgreSQL conventions

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/14-postgres-conventions`, eight commits on the `feature/multi-db` tip after the test harness merge (`5889c27a02`).
  - `0734546fb9` Create PostgreSQL string columns as text with the C collation
  - `200c496ee4` Pin PostgreSQL sessions to the UTC time zone
  - `1142107b66` Turn off JIT compilation for Jellyfin's PostgreSQL role
  - `1277c8ef28` Test that PostgreSQL shutdown closes pooled connections
  - `33d63490e6` Give long PostgreSQL identifiers stable shortened names
  - `cecb8b1507` Sort NULL values on PostgreSQL like SQLite does
  - `dff073fc18` Make PostgreSQL indexes sort NULL first
  - `dcb8537e05` Test one-or-many value filters against PostgreSQL
- **Status:** gated (first gate FAIL with one must-fix and two should-fixes, delta gate PASS WITH NITS, nits folded) and merged into `feature/multi-db`. Not pushed. Backups of earlier versions: `multidb/14-backup-a` (first version), `-b` (`f5a7553e52`, before the rebase onto the merged harness), `-c` (`bf04d79bdd`, before a Debug-only fix), `-d` (`b206ec397c`, JIT set per connection), `-e` (`35ae0c4093`, before three small fixes), `-f` (`e57bdabb30`, the version that failed the gate), `-g` (`070351a222`, before the delta-gate nits). Scratch branches `tmp-step14-rework`, `tmp-step14-round3`, `tmp-step14-round3b` are left over from rebuilding the series.

## Why it exists

The PostgreSQL provider from step 13 ran Jellyfin, but not the way SQLite runs it. Strings sorted by the server's locale, `lower()` folded accented letters, string columns enforced lengths SQLite never enforced, NULL sorted at the other end, long index names could be renamed by unrelated model changes, and date functions read whatever time zone the server had. Search queries were also many times slower than on SQLite. Each of these is a place where the same library gives different answers, or a different experience, depending on the database. This step makes PostgreSQL behave like SQLite where Jellyfin depends on it.

## What changes, commit by commit

- **Text with the C collation.** Every string column is `text` with `COLLATE "C"`. Ordering and range filters compare code points, as SQLite does, and `lower()` folds only ASCII, as SQLite's does. `text` has no length, so values longer than a declared `MaxLength` are stored whole, which is what SQLite has always done. Closes three test gaps: StringCollation (8 rows), UnicodeLower (1), StringColumnLength (2). All 11 rows pass on PostgreSQL.
- **UTC sessions.** The provider always sets `Timezone=UTC` in the connection string, replacing any value the user gave. Dates are stored as UTC, so server-side date functions must not depend on the server's time zone. Because it is a startup parameter, it survives pool resets.
- **JIT off for the role.** See the next section.
- **Shutdown test.** `RunShutdownTask` already called `NpgsqlConnection.ClearAllPools()`; nothing tested it. The test counts sessions in `pg_stat_activity` and fails if the call is removed. It runs in a non-parallel test collection because the call clears pools for the whole process.
- **Identifier lengths.** PostgreSQL keeps 63 bytes of a name. EF Core shortens long names with `~` suffixes that depend on model build order, so adding an index could rename another one. A shared convention (in `Jellyfin.Database.Implementations`, internal) shortens any name over the limit to a prefix plus eight hex characters of its SHA-256, counted in UTF-8 bytes, and rejects the model if two shortened names still collide. Default names are rebuilt from the untruncated table and column names, because EF has already cut them to 63 with a `~` before the convention sees them (found at the gate; tests now run at the real limit of 63). Today it affects six `BaseItems` index names. SQLite does not register it; MySQL (64) and SQL Server (128) will.
- **NULL ordering.** SQLite sorts NULL first ascending and last descending; PostgreSQL does the reverse. The provider plugs in a private subclass of Npgsql's query SQL generator whose `VisitOrdering` adds `NULLS FIRST` (ascending) or `NULLS LAST` (descending), except for a column that cannot be NULL in that query. Leaving the clause off non-nullable columns matters: key and unique-constraint indexes are always `ASC NULLS LAST`, and PostgreSQL does not use NOT NULL to treat the two orders as equal, so `ORDER BY "Id" NULLS FIRST LIMIT 10` lost its index scan and sorted the whole table (found at the gate, ADR-0006 R5). The generator class sits in Npgsql's `.Internal` namespace, so this file suppresses EF1001; the provider no longer uses Npgsql's non-public null-ordering switch. Closes NullOrdering (1 row).
- **NULLS FIRST indexes.** Once ORDER BY asks for NULLS FIRST on a nullable column, an ordinary ascending index (NULLS LAST) no longer matches, so the planner would add a sort. A convention gives every nullable index column the null order that matches its direction; non-nullable columns keep the default, matching the clause-free ORDER BY. Constraint-backed indexes cannot carry a sort order and are skipped. A plan test (5000 rows, `ORDER BY "Id" LIMIT 10`) pins an index scan with no sort.
- **One-or-many filters.** `WhereOneOrMany` sends a list as one array parameter on PostgreSQL. The test runs 0, 1, 2, 5 and 1000 ids and checks the ids are parameters, not literals.

## JIT: the decision and its mechanism

- **Finding.** The benchmark (ADR-0005 R7) on 100k items found correct plans everywhere, but search queries S1-S6 ran 8 to 34 times slower than SQLite. The server's `EXPLAIN ANALYZE` showed 90-99 percent of the time in JIT compilation, 220-1700 ms per query. A correlated subplan (`PrimaryVersionId`) is costed per row over the whole table, so the estimated cost (about 840k) passes `jit_above_cost`, and `postgres:16` ships with `jit = on`.
- **Proof.** With JIT off on the server only (R8), S1-S6 ran at 0.69-1.12 times SQLite; S1 went from 251 ms to 34 ms, S6 from 1330 ms to 30 ms. Plans and results did not change. `pg_trgm` is therefore not added.
- **Mechanism.** The first version ran `SET jit = off` when Npgsql opened a physical connection. Npgsql resets a pooled connection with `DISCARD ALL` when it is reused, which drops that setting, and the workaround (`No Reset On Close` for the whole pool) would have let any later session state leak between users of the pool. It was replaced. Now a new provider member, `ApplyDatabaseSettingsAsync`, runs at every startup after the schema migrations. On PostgreSQL it reads `SHOW jit` and any `jit` entry in `pg_db_role_setting` in one query:
  - JIT already off: nothing to do.
  - An administrator has set `jit` for this role, this database, this role in this database, or all roles (`ALTER ROLE ALL`): logged and left alone.
  - The server has no `jit` setting at all (`current_setting('jit', true)` is NULL): nothing to do.
  - Otherwise: `ALTER ROLE CURRENT_USER IN DATABASE "<db>" SET jit = off`, logged at Information with the command that keeps JIT on instead, and the pool for the connection is cleared so no open connection keeps the old value.
  - If any part fails, for example a read-only role, a Warning gives the exact SQL to run, and startup continues. JIT on costs speed, not correctness. The first version looked the role up with `current_user::regrole`, which lowercases names and rejects spaces and dots, and ran that query outside the error handling, so a role created as `Jellyfin` could stop the server from starting; the gate caught it, and the tests now use a role name with capitals, a space, a dot, a quote and braces.
  - Behind PgBouncer, server connections that are already open keep JIT on until the pooler replaces them.
- **Why a role setting.** A role default is applied whenever a session starts, so it survives pool resets and holds behind PgBouncer. `ALTER ROLE ... IN DATABASE` needs no ownership of the database, unlike `ALTER DATABASE`, which managed services often do not grant, and it touches only Jellyfin's role in Jellyfin's database.
- **Why at startup, not in a migration.** `pg_dump` keeps role and database settings only with `--create`. A migration would be marked as run while a restored database had lost the setting. Running at startup puts it back.
- **First start.** On a fresh install the schema migrations run before the setting exists, so they run with JIT on once.
- **API.** The member is a default no-op on `IJellyfinDatabaseProvider`, which is on the ABI list, so it is permanent. Its doc says it runs at every start, must be idempotent, and must log and continue when the server refuses the change.

## Supported connection setups

- Direct connections.
- PgBouncer in session pooling.
- PgBouncer in transaction pooling, with `No Reset On Close=true` in the connection string (Npgsql's own advice for that mode).
- PgBouncer statement pooling is not supported, because Jellyfin uses multi-statement transactions.

## What the gaps mean

Rows that group by a presentation key still fail first on the missing `min(uuid)` (GuidAggregates, 88 rows) and test nothing about ordering until step 15. So this step's evidence for collation and NULL ordering comes from its own convention tests, which order through queries that do not group by a presentation key.

## Evidence

- Builds: every commit builds alone in Release with 0 warnings; Debug has the base's 211 warnings, same sorted list, at every commit.
- Tests at the tip: SQLite Implementations 1548 passed, Server 70. PostgreSQL Implementations 1466 passed, 137 skipped, 0 failed; the skips are 88 GuidAggregates, 33 SQLite-only, 12 Windows-only, 3 NulInExactMatchParameter, 1 PostgresMigrations. Before this step PostgreSQL skipped 151; StringCollation, UnicodeLower, StringColumnLength and NullOrdering are gone, and none of their rows moved to another gap.
- JIT step: 11 PostgreSQL tests, with a role named with capitals, a space, a dot, a quote and braces; an already-off server, an administrator's explicit setting, `ALTER ROLE ALL`, a read-only role (the `ALTER` fails, 25006) and a role that cannot log in (the lookup fails) all end in the right log line and no exception. Putting `::regrole` back fails 10 of 11; ignoring the explicit setting, or not escaping braces, also fail. A unit test pins the call in `Program`.
- NULL ordering: always writing the clause fails the plan test and the non-nullable SQL test; dropping it for every column fails the LEFT JOIN test. The plan test passed 10 of 10 while the full suite ran next to it (load about 17). The gate also checked on real data that columns reached through an optional navigation, a group join and a DISTINCT subquery keep the clause.
- Identifiers: against the old convention 4 of 33 tests fail; the real model's names are unchanged.
- Shutdown: removing `ClearAllPools()` fails the test.
- Benchmark: plans pass (ADR-0005 R7); with JIT off S1-S6 run at 0.69-1.12 times SQLite (R8), so the 3x rule is met.

## Limits

- The NULL-ordering generator subclasses a type in Npgsql's `.Internal` namespace; an Npgsql upgrade can break it, and the generated-SQL tests would show that.
- The identifier convention does not cover views, routines, or constraint names of a derived type with its own table.
- An administrator who wants JIT on must set it explicitly for the role or the database; resetting the role setting alone is undone at the next start.
