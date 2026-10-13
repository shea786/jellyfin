# ADR-0003: Postgres provider baseline and migrations

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- EF Core is 10.0.11 (`Directory.Packages.props:36-39`). There are 56 SQLite migrations, from `20200514181226_AddActivityLog` to `20261002210711_MakeOwnerIdIndexesPartial`, and nobody runs Jellyfin on Postgres yet.
- Ordering: pending EF migrations and code routines are merged and the lowest ordinal ID runs first (`JellyfinMigrationService.cs:261-292`). EF migrations only join the **CoreInitialisation** stage (`:274-279`). Code-routine IDs are `Order.ToString("yyyyMMddHHmmsss")` + `_Name` (`Stages/CodeMigration.cs:18-20`); `"sss"` still prints 2 digits, so these IDs have the same 14-digit shape as EF IDs.
- Both `JellyfinMigrationAttribute` constructors set `Stage = AppInitialisation` (`JellyfinMigrationAttribute.cs:37`). The property initialiser is overridden, so **only** `UpdateNormalizedUsername` and `DisableLegacyAuthorization` actually interleave with EF migrations. Both have `RunMigrationOnSetup=false`.
- First run (`:101-130`) does **not** call `EnsureCreated`. It:
  1. creates the database if it is missing (`:113`), which on Npgsql needs `CREATEDB`;
  2. creates the history table (`:118`);
  3. writes history rows for code routines with `RunMigrationOnSetup=false` (`:104`, `:119-128`).

  EF migrations then run normally in CoreInitialisation. Before that, `PrepareSystemForMigration` calls `MigrationBackupFast` whenever any EF migration is pending, **including on a fresh install** (`:426-431`, `:476-480`). Rollback calls `RestoreBackupFast` (`:331-335`).
- Each EF migration runs in its own `IMigrator.MigrateAsync(key)` call (`:546`), which is transactional DDL on Postgres.
- `BackupService` restore deletes all history rows, re-inserts the archive's rows and then calls `PurgeDatabase` (`FullSystemBackup/BackupService.cs:166-206`).

## Options considered

- **Initial schema.** (a) Port and replay all 56 migrations. (b) One squashed `InitialCreate` generated from the current model.
- **Baseline ID.** (a) A current timestamp. (b) A timestamp that sorts before every code routine and every SQLite migration.
- **`MigrationBackupFast`.** (a) Run `pg_dump`, which needs an external binary. (b) Clone with `CREATE DATABASE … TEMPLATE`, which needs `CREATEDB` and no other sessions. (c) No snapshot, but return a sentinel key, log clearly, and rely on transactional DDL per migration.

## Decision

- **Packages.** `Npgsql.EntityFrameworkCore.PostgreSQL` **10.0.3**, which depends on EF Core `[10.0.4, 11)` and Npgsql 10.0.3. Supported floor is **PostgreSQL 16**. `UseNpgsql(cs, o => o.MigrationsAssembly(asm).SetPostgresVersion(16, 0))` makes the generated SQL independent of the server version. Tests pin `postgres:16-trixie` (ADR-0002).
- **Squashed baseline.** Postgres gets one `20200101000000_InitialCreate`, generated from the current model.
  - It sorts before every code routine and every SQLite ID, so on any install the schema exists before any CoreInitialisation routine runs.
  - A public constant `PostgresMigrationBaseline.CoversThroughSqliteMigration = "20261002210711_MakeOwnerIdIndexesPartial"` records what it replaces.
  - Every later schema change gets the **same ID** as its SQLite twin (ADR-0001). Parity test (b) compares `Postgres \ {baseline}` with SQLite IDs greater than `CoversThrough`.
  - Until the first release that contains Postgres, the baseline may be regenerated and the constant bumped. After that it is frozen.
- **Design-time factory.** `Migrations/PostgresDesignTimeJellyfinDbFactory` uses `UseNpgsql("Host=localhost;Database=jellyfin_design")`. It needs no live server for `migrations add` or `HasPendingModelChanges`. Postgres conventions go in the provider's own `OnModelCreating` and `ConfigureConventions`; parity behaviour comes from ADRs 0004-0009.
- **Provider members on `PostgresDatabaseProvider` (`[JellyfinDatabaseProviderKey("Jellyfin-PostgreSQL")]`):**
  - `Initialise`: take the connection string from `CustomProviderOptions.ConnectionString` or the override (ADR-0001). If neither exists, fail fast with a message naming both. Read options `command-timeout` (default 60), `max-pool-size` and `EnableSensitiveDataLogging`. Log the connection string with the password removed.
  - `RunScheduledOptimisation`: run `VACUUM` (never `FULL`). Run `ANALYZE` only when the library has items, which mirrors SQLite (`SqliteDatabaseProvider.cs:221-230`).
  - `RefreshStatistics`: `ANALYZE` under the same condition.
  - `RunShutdownTask`: `NpgsqlConnection.ClearAllPools()` only. The server owns durability.
  - `MigrationBackupFast`: **option (c)**. Return the sentinel key `"postgres:no-snapshot"` and log a Warning that tells the operator to take a `pg_dump` before upgrading. Each EF migration is atomic on Postgres.
  - `RestoreBackupFast(sentinel)`: log Critical. The database is left at the last fully applied migration. Then return.
  - `DeleteBackup(sentinel)`: no-op. A `CREATE DATABASE … TEMPLATE` clone may come later as an opt-in `Options` entry, in its own ADR.
  - `PurgeDatabase`: run one `TRUNCATE TABLE "t1", "t2", … RESTART IDENTITY CASCADE`, with every identifier quoted and any schema prefix split off.

## Consequences

- Fresh Postgres install: the database already exists (recommended: pre-created and owned by the jellyfin role, so `CREATEDB` is not needed), history is seeded, the baseline runs atomically, then later migrations and routines run. No SQLite-only routine runs, because those routines are `RunMigrationOnSetup=false` and get pre-marked.
- **Moving an existing SQLite server is backup → switch → restore. Simply switching is refused** (`:198-240`). Restore currently copies the archive's **SQLite** EF history rows. That leaves the Postgres baseline "pending" and identity sequences behind the restored IDs. The restore fix in `pr-plan.md` must:
  - keep the archive's code-routine rows;
  - keep the target provider's EF rows;
  - reseed sequences through an additive provider member that defaults to a no-op.
- The fast backup gives weaker protection than on SQLite. Code routines are not transactional, so a routine that fails part way can leave partial data behind. That is why the documentation tells operators to run `pg_dump` first.
- **Suspected bug, confirmed (not fixed here): `JellyfinMigrationService.cs:121`.** The code is `.Where(e => !appliedMigrations.Any(f => f != e.BuildCodeMigrationId()))`. That is true only when the history is empty or contains nothing but that ID. The intended expression is `!appliedMigrations.Contains(id)`.
  - It is harmless on a normal first run on Postgres: the database is empty, so every routine is seeded.
  - It breaks when the history is non-empty and the server is in setup or `--mode SeedSystem`. Examples: an upgrade before the wizard was finished, or a pre-created database with leftover history. In those cases **nothing** is pre-marked, and routines added later run against a fresh database.
  - The fix is a separate PR with a test.

## Amendments (orchestrator review, 2026-10-12)

**A1. Migration safety net on Postgres (supersedes the "warning only" stance on `MigrationBackupFast`).** Option (c) stays: there is no in-process snapshot. It is now backed by transactional DDL, made explicit and enforced:

- **Per-migration atomicity is pinned by a test, not assumed.** A Postgres migration test applies a deliberately failing migration from a test-only migrations assembly. It asserts that no partial schema and no `__EFMigrationsHistory` row remain, and that the database is still at the last good migration. If EF's transaction behaviour changes, this test fails.
- **Non-transactional migrations are banned on Postgres.** The SQLite provider ignores `RelationalEventId.NonTransactionalMigrationOperationWarning` (`SqliteDatabaseProvider.cs:86-88`). The Postgres provider configures it as **Throw**. A Postgres migration therefore cannot use `suppressTransaction: true` or `CREATE INDEX CONCURRENTLY` without an explicit, reviewed exception.
- **Failure handling:**
  - If a migration fails, startup still fails hard (existing behaviour).
  - The `RestoreBackupFast(sentinel)` Critical log names the last applied migration and tells the operator to restore their `pg_dump`.
  - The pre-upgrade Warning is emitted **only when the database already has applied history**. A fresh install has nothing to lose, so it stays quiet.
- **Residual risk, which the explainer must state plainly:**
  - An upgrade spanning several migrations is atomic *per migration*, not per upgrade.
  - Code routines are not transactional.
  - Recovery after a mid-upgrade failure is the operator's `pg_dump`. We decided against an automatic `CREATE DATABASE … TEMPLATE` clone: it needs `CREATEDB` and exclusive access to the database, which a pooled server does not have at that point. It can return later as an opt-in `Options` flag.

**A2. Supported floor PostgreSQL 16 (provisional until ADR-0005/0006 land).**

- Rationale:
  - PG 14 reaches end of life in November 2026, before any release of this work could ship.
  - PG 16 is supported until November 2028, which covers the expected life of the first release that contains Postgres.
  - One floor keeps the test matrix to two images: the floor in the required job and the latest in the nightly job (ADR-0010).
- Nothing in ADRs 0001-0003 needs a feature newer than 15 (`CREATE DATABASE … STRATEGY` is 15+). If the parity ADRs need 17 (for example the builtin `C.UTF-8` collation provider) or 18 (`LIKE` on nondeterministic collations), the floor is raised **here**, and the test image and `SetPostgresVersion` change with it.

## Amendments (multi-target re-check, 2026-10-12)

**S1. Migration safety net, per engine.** A1 assumed transactional DDL. The probe shows that holds on SQLite, PostgreSQL and SQL Server. On MySQL and MariaDB, DDL always commits implicitly. CockroachDB (rejected) would need `autocommit_before_ddl=false`.

- **SQLite:** transactional DDL plus a file snapshot. Unchanged.
- **PostgreSQL:** A1, unchanged.
- **SQL Server:** the same as A1.
  - `NonTransactionalMigrationOperationWarning` is set to Throw.
  - It has the same failing-migration test.
  - The sentinel backup key is `"sqlserver:no-snapshot"`.
- **MySQL/MariaDB:** a failed migration leaves a partial schema and no history row. The next start re-runs the migration, which then fails with a misleading "already exists" error.
  - **New additive member:** `bool SupportsTransactionalMigrations => true`. The MySQL provider returns `false`.
  - **When the flag is false,** `JellyfinMigrationService` does the following around each EF migration (`:546`):
    - before the migration, it writes `migration-in-progress.json` (migration ID, provider key, UTC time) in the data directory;
    - after the history row exists, it deletes the file.
  - **At startup, if the file exists,** the service refuses to migrate. It logs Critical, names the migration, and tells the operator to restore their `mysqldump`/`mariadb-dump` and delete the file.
  - The marker is a file because after a partial migration the database's state is the thing we cannot trust.
  - The pre-upgrade Warning is the same as A1.
  - **Review rule:** a MySQL migration performs one schema operation per statement wherever EF allows.
  - **Test:** a deliberately failing migration leaves the marker, and the next startup refuses with the message. A successful run leaves no marker.

**S2. Explicit identity values on restore, and reseeding afterwards.** This supersedes the Consequences sentence about reseeding through one member. There are two additive members, and both default to a no-op:

- **`ValueTask<IAsyncDisposable> BeginExplicitIdentityInsertAsync(JellyfinDbContext context, IEntityType entityType, CancellationToken)`.**
  - SQL Server runs `SET IDENTITY_INSERT <table> ON` on the context's open connection, and `OFF` on dispose.
  - SQL Server allows only one such table per session, which is why the scope is per table.
- **`Task ResetIdentitySequencesAsync(JellyfinDbContext context, IEnumerable<IEntityType> entityTypes, CancellationToken)`.**
  - PostgreSQL: `setval(pg_get_serial_sequence(...), COALESCE(MAX(id), 0) + 1, false)` for each identity column. `TRUNCATE … RESTART IDENTITY` alone is not enough after inserts with explicit IDs.
  - MySQL/MariaDB, SQL Server and SQLite: no-op, because they advance the identity themselves (probe).

**The restore flow changes** (`BackupService.cs:199-250`). Today it adds every table to one context and saves once (`:249`). The new flow:

1. Open the connection.
2. **Pre-flight, before `PurgeDatabase`:** deserialise and validate the archive, including the key-length bounds from ADR-0009 L2. A restore that would fail therefore no longer empties the database first.
3. Purge.
4. For each table: open the identity scope, add the rows, call `SaveChanges`, close the scope.
5. Call `ResetIdentitySequencesAsync`.

The work runs in one transaction where the engine allows it. On MySQL, `TRUNCATE` commits implicitly.

**`PurgeDatabase` per engine:**

- **PostgreSQL:** as decided above.
- **MySQL/MariaDB:** `SET FOREIGN_KEY_CHECKS=0`, then `TRUNCATE` each table, which also resets `AUTO_INCREMENT`.
- **SQL Server:** `DELETE` in reverse foreign-key order, then `DBCC CHECKIDENT … RESEED`.
  - `CHECKIDENT` behaves differently on tables that never held a row, so the value must be chosen per case.
  - **Test, on every engine:** the first identity after a purge is 1, both for tables that held rows and for tables that never did.

## Amendments (upstream reconciliation, 2026-10-12)

- **U1. The restore hook uses upstream's name. This supersedes the name `ResetIdentitySequencesAsync` in S2.**
  - Open upstream #17901, "Fix/restore generated ids", adds `Task CompleteDatabaseRestoreAsync(JellyfinDbContext dbContext, CancellationToken cancellationToken) => Task.CompletedTask` to `IJellyfinDatabaseProvider`, for realigning sequences after a restore. Its author tested it against a PostgreSQL plugin. It depends on #17899, "Roll back failed database restores", which validates the archive first and restores in a transaction.
  - We use exactly that name and signature. If theirs lands first, ours merges into it or disappears.
  - Step 08 starts by re-checking #17899 and #17901. Whatever has merged is adopted, and our step only adds what is still missing: preserving history rows (S2) and `BeginExplicitIdentityInsertAsync` for SQL Server. Pre-flight before the purge is dropped from our step if #17899 already provides it.
  - Open issue #17449: `--restore-archive` crashes on a brand-new instance because no schema exists yet. Step 08 covers that case with a test, or explains in its explainer why not.
- **U2. Baseline inputs confirmed or added.**
  - `StripEmbeddedLinkedChildren` (`json_remove`/`json_valid`) is confirmed as SQLite-only SQL, handled in step 09.
  - The latest SQLite migration is still `20261002210711_MakeOwnerIdIndexesPartial`.
  - The `IX_Peoples_NameLower` index is created by raw SQL in a code routine. The Postgres baseline creates it as a functional index on `lower("Name")`, and the routine must be provider-neutral or a no-op when the index exists (step 09).
- **U3. Watch #18091, "Enforce database integrity".** It adds foreign keys on `TrickplayInfos` and `MediaSegments` and a new SQLite migration, turns on `PRAGMA foreign_keys`, and splits `DeleteItems` out of `DeleteItem`. If it lands, the Postgres baseline is regenerated and the `CoversThroughSqliteMigration` pin moves.

## Amendments (step 08 restore, 2026-10-13)

- **R1. History rows from an archive are classified by the server's code routine ids. This refines S2.**
  - Step 08 first told routines apart from schema migrations by membership in the target's migrations assembly. A cross-engine restore (SQLite archive into PostgreSQL) then copied all 56 SQLite schema migration ids into the PostgreSQL history, because the PostgreSQL assembly holds only its baseline. The EF migrator ignores unknown ids today, but one that matched a later PostgreSQL migration would make it skip that migration silently.
  - Decision: only archive history rows whose id is a code routine id known to this server are copied. Schema migration ids from an archive are never copied, whatever engine wrote them; the target builds its schema from its own migrations. Archive rows for routines this server no longer has are not copied either, as nothing would ever run them.
- **R2. The seam is a public interface `IMigrationRoutineCatalog { IReadOnlySet<string> RoutineIds }` in `Jellyfin.Server.Implementations`, implemented in `Jellyfin.Server` and registered in `CoreAppHost`.**
  - The routines and `JellyfinMigrationAttribute` live in `Jellyfin.Server` (assembly `jellyfin`), which `Jellyfin.Server.Implementations` cannot reference. `BackupService` is public, is registered from `Emby.Server.Implementations`, and is built by the DI container, which needs a public constructor, so its parameter types must be public. An internal interface plus `InternalsVisibleTo("jellyfin")` would also force `BackupService` and its registration to change visibility, which is a larger change.
  - Rejected alternatives: telling rows apart by `ProductVersion` (EF writes its own version, routines write the server version; both look like version strings and the distinction is undocumented); scanning the entry assembly by attribute name from `Jellyfin.Server.Implementations` (reflection on an assembly it does not reference); adding a parameter to `IBackupService.RestoreBackupAsync` (`MediaBrowser.Controller` is on the ABI list).
  - `Jellyfin.Server.Implementations` is not on the ci-compat ABI list, so the interface is not permanent API. It has one read-only member and no plugin use is intended.
- **R3. PostgreSQL identity reseeding after a restore belongs to the PostgreSQL provider.** Step 08's end-to-end proof showed that the restore itself works on PostgreSQL 16.15, but the next insert into a table with an identity column fails with 23505 until each sequence is set to max(id)+1. The PostgreSQL provider implements `CompleteDatabaseRestoreAsync` with `setval`, with a test, in the PostgreSQL stack (evidence and draft in `/tmp/s08-pg-restore-proof/`).

## Amendments (PostgreSQL benchmark, 2026-10-13)

- **R4. The PostgreSQL provider turns JIT compilation off for its sessions.**
  - Evidence: the R1(ii) benchmark (ADR-0005 R7, `/tmp/s05-bench/R1ii-summary.txt`) found correct plans everywhere, but search queries S1-S6 ran 8-34 times slower than SQLite. The server's own `EXPLAIN ANALYZE` showed 90-99 percent of that time in JIT compilation (220-1700 ms per query); the scan nodes took 16-25 ms, close to SQLite. The correlated `PrimaryVersionId` subplan is costed per row over the whole table, which pushes the total cost (about 840k) over `jit_above_cost` and both inline/optimize thresholds. `postgres:16` ships with `jit = on`.
  - Decision: every session the provider opens runs with `jit = off`. Jellyfin's queries are short OLTP-style queries that never run long enough for compiled expressions to pay back their compile time. As with `Timezone=UTC`, the provider decides this for the user. No setting turns it back on, because no query of ours benefits.
  - Mechanism: `SET jit = off` once per physical connection, through Npgsql's physical connection initializer (`ConfigureDataSource(b => b.UsePhysicalConnectionInitializer(...))` on `UseNpgsql`). Rejected: the startup parameter `Options=-c jit=off`, because PgBouncer rejects the `options` startup parameter by default, which would break connections for users behind a pooler. Also rejected: a `SET` on every logical open (one extra round trip per context), and `ALTER DATABASE`/`ALTER ROLE` (the provider must not change server objects it does not own).
  - Placement: one commit in the step 14 session-settings group, right after the UTC session commit. It comes with a unit test for the configured data source and a PostgreSQL test that reads `SHOW jit` from a pooled connection, both after a reopen and on a fresh physical connection.
  - pg_trgm stays out unless the S1-S6 3x rule still fails with JIT off.
