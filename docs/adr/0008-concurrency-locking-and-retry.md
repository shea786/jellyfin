# ADR-0008: Concurrency tokens, locking strategy and retry policy

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- **Concurrency tokens.**
  - `uint RowVersion` is declared with `[ConcurrencyCheck]` in `User.cs:305-306`, `ActivityLog.cs:114-115` and other entities (`IHasConcurrencyToken.cs:11`).
  - The app increments it in `OnSavingChanges` (`User.cs:345`, `ActivityLog.cs:120`), called from `JellyfinDbContext.cs:314-322`.
  - It is **not** `IsRowVersion`. Npgsql maps `uint` to `bigint` (verified DDL `"RowVersion" bigint NOT NULL`), and EF's `WHERE RowVersion = @orig` works the same way on both engines.
- **Locking.**
  - The default is `NoLock` (`ServiceCollectionExtensions.cs:97-101`).
  - `OptimisticLockBehavior` retries only on SQLite message text (`OptimisticLockBehavior.cs:56-65`).
  - `Pessimistic` is a process-wide reader/writer lock.
  - ADR-0001 makes `NoLock` the PG default and logs a warning for the other modes.
- **Read-then-insert races.**
  - `ItemPersistenceService.cs:376-388` reads the `ItemValues` that exist, then inserts the missing ones inside the transaction at `:334`, against the unique `(Type, Value)` index (`ItemValuesConfiguration.cs:17`). Under PG's `READ COMMITTED`, the losing writer blocks and then fails with `23505`. SQLite with `NoLock` also fails the losing save, with BUSY or a UNIQUE error.
  - `ReconcileUserDataAsync` deletes and re-inserts rows keyed by `(ItemId, UserId, CustomDataKey)` (`UserDataConfiguration.cs:15`).
  - `Peoples` has no unique name (`PeopleConfiguration.cs:15`). Duplicates are tolerated by design (`PeopleRepository.cs:189-190`).
- **Explicit transactions** are opened at `ItemPersistenceService.cs:59,164,230,334` and `PeopleRepository.cs:163`.

## Options considered

- **A. Npgsql `EnableRetryOnFailure`.** EF rejects user-initiated `BeginTransaction` outside `strategy.Execute`, so every transaction site above would need restructuring. It is also blind to unique-violation races.
- **B. Map `RowVersion` to `xmin`.** It conflicts with the app-side increment and has no equivalent on other engines.
- **C. Make the lock behaviours provider-aware.** They exist for SQLite's single writer; server engines don't need them.
- **D. Targeted retry of idempotent units of work** on classified write conflicts, with classification done by an operation-named provider member.
- **E. `INSERT … ON CONFLICT DO NOTHING`.** It needs per-dialect SQL (`INSERT IGNORE`, `MERGE`). Rejected until a dialect abstraction is justified.

## Decision

- **`RowVersion` is unchanged:** `uint` with `[ConcurrencyCheck]`, the app-side increment, and `bigint` on PG. `DbUpdateConcurrencyException` handling stays the same (`JellyfinDbContext.cs:302-306`).
- PG uses **`NoLock`** and `READ COMMITTED`. Shared code never raises the isolation level.
- **New provider member:** `bool IsRetryableWriteConflict(Exception exception)` on `IJellyfinDatabaseProvider`. Its default implementation returns `false`, so plugin providers are unaffected.
  - SQLite: a `SqliteException` with extended code 2067 (UNIQUE) or 1555 (PRIMARYKEY).
  - PG: `PostgresException.SqlState` `23505`, `40001` or `40P01`.
- **Shared helper** `WriteConflictRetry.ExecuteAsync(...)` in `Jellyfin.Server.Implementations`:
  - at most 3 attempts, with 50-250 ms jittered back-off;
  - every attempt gets a **new `DbContext` and transaction** and re-reads existing rows;
  - a warning is logged on each retry.
  It wraps `UpdateOrInsertItems` and `ReconcileUserDataAsync`. Every unit it wraps must be idempotent, and that is documented on the helper.

## Consequences

- One deliberate SQLite change: a save that loses the `ItemValues` race now retries instead of failing the item. No migration.
- PG deadlocks (`40P01`) in the wrapped paths retry. Elsewhere they surface as they do today. `OptimisticLockBehavior`'s message match is SQLite-specific and never fires on PG, which is harmless.
- The interface gains one member. Future providers map their own codes, for example MySQL 1062/1213 and SQL Server 2627/2601/1205.
- `xmin`-based tokens remain possible later, behind a new ADR.

## Tests required

**(a) SQLite, in the portability PR:**

1. `ConcurrencyToken_SecondStaleSaveThrows`: load a `User` in two contexts and save both. The second save throws `DbUpdateConcurrencyException`, and `RowVersion` went up by exactly 1.
2. `ItemValues_ConflictingInsertBetweenReadAndWrite_Retries`: an `IDbCommandInterceptor` passed to the fixture constructor inserts the same `(Type, Value)` right after the existing-values `SELECT`. Assert that the save succeeds, there is exactly one `ItemValues` row, and the item maps to it.
3. `SqliteDatabaseProvider.IsRetryableWriteConflict`: true for UNIQUE and PRIMARYKEY. False for FOREIGNKEY, NOTNULL and arbitrary exceptions.

**(b) Postgres:**

- 1-2 unchanged;
- unit tests for the PG classifier: 23505, 40001 and 40P01 are true; 23503 and 22001 are false;
- `[PostgresFact]`: 8 parallel saves of distinct items that share a new genre all succeed and create one row;
- `[PostgresFact]`: the `RowVersion` columns are `bigint`.

## Amendments (multi-target re-check, 2026-10-12)

- **R1. Correction: which entities carry `RowVersion`.**
  - The mapped model has four: `User`, `ActivityLog`, `Permission` and `Preference`.
    - Verified: `IsConcurrencyToken()` appears exactly four times in `Providers.Sqlite/Migrations/JellyfinDbModelSnapshot.cs`, at `:73` (ActivityLog), `:1086` (Permission), `:1115` (Preference) and `:1375` (User).
  - `Group` (`Entities/Group.cs:48`) and 18 types under `Entities/Libraries/` also implement `IHasConcurrencyToken`, but they are not in the model.
  - A list that names `Group` as mapped is wrong.
  - The mechanism is the same on every engine. Store types: PostgreSQL `bigint`, MySQL/MariaDB `int unsigned`, SQL Server `bigint`.
- **R2. Classifier codes per provider.**
  - **Retryable:**
    - MySQL/MariaDB: 1062 (duplicate key), 1213 (deadlock), 1205 (lock wait timeout);
    - SQL Server: 2627 and 2601 (unique), 1205 (deadlock victim), 1222 (lock request timeout), 3960 (snapshot update conflict).
  - **Not retryable, used as negative controls in the unit tests:**
    - foreign-key violations: MySQL 1451/1452, SQL Server 547;
    - data too long: MySQL 1406, SQL Server 2628/8152.
- **R3. Every server engine runs at READ COMMITTED, as PostgreSQL does.**
  - **MySQL/MariaDB** default to REPEATABLE READ, whose gap locks raise the deadlock rate of the `ItemValues` insert race.
    - The provider sets the session isolation level in a connection interceptor, on open.
    - The same interceptor sets a strict `sql_mode` (ADR-0009 L2).
  - **SQL Server's** READ COMMITTED uses locking reads, so readers queue behind library-scan writers.
    - At `Initialise`, the provider checks `is_read_committed_snapshot_on`. If it is off, the provider logs a Warning recommending `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON`.
    - The provider does not run that itself, because the command needs exclusive access to the database.
    - CI databases have it on.
- **R4. CockroachDB (rejected) for the record:** it runs SERIALIZABLE only, so it would need retry around every transaction, not just the idempotent units of work.
