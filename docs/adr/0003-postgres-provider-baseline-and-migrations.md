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
