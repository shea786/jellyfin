# ADR-0008: Concurrency tokens, locking strategy and retry policy

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- **Concurrency tokens.**
  - `uint RowVersion` is declared with `[ConcurrencyCheck]` in `User.cs:305-306`, `ActivityLog.cs:114-115` and other entities (`IHasConcurrencyToken.cs:11`).
  - The app increments it in `OnSavingChanges` (`User.cs:345`, `ActivityLog.cs:120`), called from `JellyfinDbContext.cs:314-322`.
  - It is **not** `IsRowVersion`. Npgsql maps `uint` to `bigint` (verified DDL `"RowVersion" bigint NOT NULL`), and EF's `WHERE RowVersion = @orig` works the same way on both engines.
- **Locking.**
  - The default is `NoLock` (`DatabaseLockingBehaviorTypes.cs:11`, and the fallback configuration at `ServiceCollectionExtensions.cs:141-146`).
  - `OptimisticLockBehavior` retries only on SQLite message text (`OptimisticLockBehavior.cs:56-65`).
  - `Pessimistic` is a process-wide reader/writer lock.
  - ADR-0001 makes `NoLock` the default for every provider. At the time this was written it also said that the other modes are honoured with a warning; ADR-0001 L1 and S8 at the end of this ADR correct that.
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

## Amendments (upstream reconciliation, 2026-10-12)

- **U1. `WriteConflictRetry` follows the shape of open upstream #17119.**
  - #17119 makes the ItemValues get-or-create safe under parallel saves. When a re-read shows the missing rows now exist, it retries the whole `UpdateOrInsertItems` unit of work.
  - An upstream reviewer rejected `ON CONFLICT` there as not portable to MariaDB. We take the same position: retry the unit of work after a re-read, and use no engine-specific upsert.
  - Step 07 does not build a second ItemValues retry. If #17119 lands first, step 07 uses its retry rather than wrapping it.
- **U2. Existing ad-hoc retries stay as they are**: `DisplayPreferencesManager.cs:45-50` and `:123`, `ItemPersistenceService.cs:201`, `SessionManager.cs:274`. Moving them onto `WriteConflictRetry` would be an unrelated change, so step 07 only does it where it already edits that method. The rest are listed as a possible follow-up.
- **U3. Watch #17115.** It reworks SQLite pessimistic locking and touches the locking classes that the `NoLock` default relies on.

## Amendments (step 07, 2026-10-13)

- **S1. On SQLite the retry does not run.** The EF transaction takes SQLite's write lock at `BEGIN`; another connection's `INSERT` gets `SQLITE_BUSY` immediately (probed), so a read-then-insert race cannot happen and no unique violation from a concurrent writer reaches the retry. The Consequences sentence "a save that loses the ItemValues race now retries instead of failing" therefore describes engines with concurrent writers, not SQLite. The tests stage the race on the attempt's own connection to raise a real constraint violation, which is valid for testing the retry.
- **S2. BUSY and LOCKED are not classified as retryable** because `OptimisticLockBehavior` and the busy timeout already retry the command, and BUSY never follows a stale read. A unique violation caused by a bug is retried twice and then rethrown.
- **S3. Test (a)1, `ConcurrencyToken_SecondStaleSaveThrows`,** is added to step 07 as its first commit, pinning existing behaviour.

## Amendments (step 24 gate, 2026-10-14)

- **S4. MySQL and MariaDB 1205 (lock wait timeout) stays retryable.** With `innodb_rollback_on_timeout` off, which is the default, only the statement is rolled back and the transaction stays open with its locks. The retry does not continue that transaction: every attempt gets a new context and a new transaction, and the failed one is rolled back when its context is disposed. Tests on both servers show that only the statement rolls back, that the error is classified as retryable, and that the retry runs the work again in a new transaction.
  - **Worst case.** Three attempts each wait out `innodb_lock_wait_timeout`, so a save behind a lock that is never released fails after about three times that setting: about 150 seconds at the default of 50.

## Amendments (step 27a, 2026-10-15)

- **S5. Correction to R3: on MySQL and MariaDB, the level of the session does not reach the transactions.**
  - MySqlConnector 2.6.2, which Microting.EntityFrameworkCore.MySql 10.0.12 uses, starts a transaction that names no level at REPEATABLE READ explicitly (`MySqlConnection.cs:195-205` at tag 2.6.2). It sends `set session transaction isolation level repeatable read` before `start transaction` (`:239-250`), so the session stays at REPEATABLE READ after the transaction, until the connection is reset.
  - Jellyfin starts its transactions without a level, and so does EF Core for a save and a migration. All of them ran at REPEATABLE READ, despite the READ COMMITTED that the connection interceptor of R3 sets on open. Probed on MySQL 8.4.11 and 9.7.2, and MariaDB 11.4.13, 11.8.9 and 12.3.3.
  - On MariaDB 11.8 and later, `innodb_snapshot_isolation` is on by default, and a write to a row that another connection changed after the transaction read it then fails with error 1020 (`ER_CHECKREAD`). `LibraryStructureControllerTests.RenameLibrary_UserHasLibraryPreferences_RemapsLibraryId` failed that way, now and then, on 11.8.9 and 12.3.3.
    - Ten runs of its test class on each server, before the fix: 7 failures on 12.3.3 and 5 on 11.8.9, all with 1020. With the fix to the isolation level alone, and with the retry of S6 added: no failure in 20 runs either way.
  - **Fix.** The provider replaces the EF Core connection of Microting with `ReadCommittedConnection`, which turns `IsolationLevel.Unspecified` into `ReadCommitted` in `BeginTransaction` and `BeginTransactionAsync`. A level that is named is kept. The master connection the provider creates for `CREATE DATABASE` and `DROP DATABASE` is not replaced, as it starts no transaction, and the migration lock of the provider is a named lock (`GET_LOCK`), not a transaction. MySqlConnector then sets READ COMMITTED for the session, so the session is still at READ COMMITTED after the transaction. The connection interceptor still sets the level on open, for the statements outside a transaction.
  - **Why not a transaction interceptor.** The level is chosen before EF Core raises `TransactionStarting`. Every interceptor sees the same event data, and its level is read-only. The interceptors of the provider run before the ones of the locking behaviour, which `ServiceCollectionExtensions` adds afterwards. `OptimisticLockBehavior` starts the transaction itself, with the level of the event, so an interceptor of the provider could neither change that level nor start the transaction in its place: MySqlConnector refuses a nested transaction. Shared code is not changed.
  - **Tests**, on both servers: the level the server reports inside a transaction started without a level, and on the session after commit and rollback, synchronous and asynchronous; a pooled connection reused after its reset; each locking behaviour, added after the provider as the server does; the transactions EF Core starts for a save and a migration, the session after the migration, and the transaction of the provider purge; and a row that another connection commits during the transaction is visible in it and can be updated.
- **S6. MySQL and MariaDB 1020 is retryable (amends R2), as defence in depth.** At READ COMMITTED it does not occur. A transaction that names REPEATABLE READ would still raise it under snapshot isolation, and the retry runs the work again in a new context and transaction, as for 1205 (S4). A MariaDB test turns snapshot isolation on for its session, so it also runs on 11.4, provokes a real 1020, and checks that it is classified as retryable.

## Amendments (step 27, 2026-10-16)

- **S7. Correction to R3 for SQL Server: a connection keeps the isolation level of its last user for statements outside a transaction; SqlClient names READ COMMITTED for a transaction that names no level.**
  - **Measured, the session.** In the SQL Server spike, on 2022 CU27 and 2025 CU9 + GDR (`evidence/probes-2022/probes.txt` and `evidence/probes-2025/probes.txt`, lines 16 and 17), a connection that one user set to SERIALIZABLE, with `SET TRANSACTION ISOLATION LEVEL` or by a transaction that named the level, is still SERIALIZABLE when SqlClient hands it out again from the pool. So a statement outside a transaction could run at a level that some earlier user chose.
  - **Measured, the unnamed transaction (step 27).** With SqlClient 6.1.6 and EF Core 10.0.12 on SQL Server 2022 CU27, a transaction that names no level runs at READ COMMITTED while the session is at SERIALIZABLE. SqlClient names that level itself, not the server: `SqlInternalConnection.BeginSqlTransaction` changes `IsolationLevel.Unspecified` to `ReadCommitted` and sends it ([`SqlInternalConnection.cs`, tag v6.1.6, lines 239 to 242](https://github.com/dotnet/SqlClient/blob/v6.1.6/src/Microsoft.Data.SqlClient/src/Microsoft/Data/SqlClient/SqlInternalConnection.cs)). The `Iso -1` in the trace of the diagnostic is the value that `SqlConnection.BeginTransaction` was entered with, written before that change; the transaction reports `ReadCommitted` (`dbtx=ReadCommitted`). It held after a Serializable commit, after a bare `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE`, on a pooled reopen of the same session still at SERIALIZABLE, and in raw SqlClient without EF Core (`/tmp/27/fix/isolation/diag/diag.txt`). The session is at READ COMMITTED after such a transaction. The new theories below pass on SQL Server 2025 CU9 + GDR (17.0.5015) too (`/tmp/27/dev/s10-sql2025`, at `c12a641533`, and `/tmp/27/fix2/sessions-2025`, at `d6d0951e9c`).
  - **One remedy, in the provider.** `SessionSettingsInterceptor` runs `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` every time a connection opens, so a statement outside a transaction on a reused connection runs at READ COMMITTED. The spike showed that a change to how transactions start does not reach such a statement: it stayed at SERIALIZABLE (`probes-c3-*`). A transaction that names no level needs nothing of the provider.
  - **No connection replacement, and why not.** The provider does not replace `ISqlServerConnection`, as S5 does for MySQL. It would have no effect, because SqlClient already names READ COMMITTED for such a transaction, and it would depend on an internal EF Core API (EF1001). MySQL differs: MySqlConnector names REPEATABLE READ (S5).
  - **What remains.** A connection that Jellyfin holds open and on which code names SERIALIZABLE keeps that level for the statements outside a transaction until it is opened again. No code names a level today. A transaction that names a level keeps it.
  - **The RCSI check runs at startup, not at `Initialise`.** `Initialise` must not connect: the design-time factory has no server, and the database may not exist yet. The provider reads `is_read_committed_snapshot_on`, and the default collation, in `ApplyDatabaseSettingsAsync`, which runs at every start after the migrations, each value as a single scalar. It logs a Warning that names the `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` to run. If a value cannot be read, it logs that and does not throw, except for a cancellation.
  - **The server floor is refused when a connection opens**, from the version the server reports in the handshake. The edition of the engine is read once for each server: Azure SQL Database, Azure SQL Managed Instance and Azure Synapse are refused with a message of their own (ADR-0011 F9).
  - **Tests** (step 27), on SQL Server, in `SqlServerSessionTestsBase`, run by `SqlServerSessionTests` and `SqlServerLockingReadsSessionTests`:
    - A connection left at SERIALIZABLE and returned to the pool, then an untransacted EF query (synchronous and asynchronous), an EF transaction without a level (commit and rollback) and a save, each reading `transaction_isolation_level` of its session inside the same batch or the same transaction. All run at READ COMMITTED.
    - After a transaction that named SERIALIZABLE on the open connection, committed or rolled back, the next transaction without a level and a save each run at READ COMMITTED. These two theories pin what SqlClient sends for an unnamed transaction. If a later SqlClient started such a transaction at the level of the session, they fail.
    - A transaction that names a level keeps it.
    - The four session settings the queries rely on, and a save under each locking behaviour read back at READ COMMITTED.
    - The RCSI warning: a database that reads with locks (RCSI off) is warned of and told what to run, and one that reads from row versions is not. Also the default-collation warning, and no throw when the database cannot be read.

## Amendments (X13, 2026-10-17)

- **S8. Locking: `Pessimistic` is refused on a provider that does not support it. This supersedes the "Locking." context bullets above.**
  - The default is still `NoLock` (`ServiceCollectionExtensions.cs`, the `switch` on `LockingBehavior`).
  - `OptimisticLockBehavior` still retries only on SQLite message text, and a server provider that is set to `Optimistic` accepts it. No provider reads `LockingBehavior`, and `OptimisticLockBehavior.Initialise` logs at Information ("The database locking mode has been set to: Optimistic.", `OptimisticLockBehavior.cs:83`), not a warning.
  - `Pessimistic` is a process-wide reader/writer lock, and the server now **refuses to start** with it on PostgreSQL, MySQL/MariaDB and SQL Server (`SupportsPessimisticLocking` is `false` there; ADR-0001 L1 and L2). The check is `EnsureLockingBehaviorIsSupported`, called right after the provider is created, with the message "LockingBehavior in database.xml is Pessimistic, which the database provider '…' does not support. Set LockingBehavior to NoLock, the default, or Optimistic."
  - ADR-0001 L1 holds the reason: a thread-affine `ReaderWriterLockSlim` held across an `await` can deadlock the writers. ADR-0001 previously said that the server provider "honours them but logs a warning". For `Pessimistic` that is no longer true (it is refused), and for `Optimistic` the log line was always Information, not a warning.
- **S9. The tests that ran a server engine under `Pessimistic` dropped that row.**
  - **SQL Server.** `SqlServerSessionTestsBase.SaveChanges_WithEachLockingBehaviour_RunsAtReadCommittedAndIsReadBack` (S7) no longer has the `PessimisticLockBehavior` row. The test is now asynchronous, because the synchronous form existed only to keep that behaviour from hanging (pr-plan B37). It runs `NoLockBehavior` and `OptimisticLockBehavior`.
  - **MySQL and MariaDB.** `MySqlTransactionIsolationTests.BeginTransaction_UnderEachLockingBehaviour_RunsAtReadCommitted` (S5) no longer has the `Pessimistic` row, and the matching `switch` arm is gone.
  - **Why.** A server engine can no longer be started under `Pessimistic`, so the row would test a state the server refuses. `LockingBehaviourRegistrationTests` pins the refusal instead, on every engine, with the exact message parts.
