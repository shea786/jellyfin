# Explainer: step 22, a leased pool of test databases

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/22-harness-leased-pool`, three commits on the `feature/multi-db` tip after step 11 (`fccca748c7`).
  - `67d045429d` Let a test ask the backend for a fresh database
  - `dc0b7fa319` Add a pool of test databases for engines that cannot copy them
  - `a966af02e3` Reuse pooled test databases after resetting them
- **Status:** gated (FAIL on S1, S2 and S4, all folded), re-gated PASS WITH NITS, merged into `feature/multi-db` after step 21. Not pushed. Backup of the first gated version: `multidb/22-backup-a` (`39e77c9073`).
- **Test code only.** Nothing under `src/` or `Jellyfin.Server/` changes, so nothing here reaches a running server.

## Why it exists

Every test gets a database of its own. SQLite copies an in-memory template with the backup API, and PostgreSQL clones a template database. MySQL, MariaDB and SQL Server cannot clone a database cheaply, and building the schema from the migrations for each of roughly 1,600 tests would take far too long. Their backends (steps 24 and 27) need another way: build a database once, and when a test is done with it, empty it and hand it to the next test. This step adds the part of that which does not depend on the engine, and proves it on SQLite files so it can be tested without a server.

## What changes, commit by commit

- **`fresh` flag.** `ITestDatabaseBackend.Create(..., fresh)` and a `DatabaseTestFixture` overload let a test ask for a pristine database it alone uses. A test that changes the schema, such as one that runs migrations, needs that. SQLite and PostgreSQL already give every test a new database, so for them the flag changes nothing.
- **The pool.** `LeasedDatabasePool` creates databases on a server through `IPooledDatabaseServer`, tracks them, and drops all of them when it is disposed or the process exits. `SqliteFileTestBackend` implements the server interface with SQLite files, which is how the pool is tested. In this commit every lease is still a new database.
- **Reuse after reset.** When a lease is disposed, `DatabaseReset` purges every table of the model, which also restarts the identity counters, and inserts the model's seed data again. The seed data is read from the design-time model, since the runtime model does not keep it. `__EFMigrationsHistory` is not in the model, so it survives. The reset runs on dispose, so a failed test still returns its lease. A lease that cannot be reset is dropped and the failure reported, never reused. The pool creates a database whenever none is waiting, so it grows to at most the number of tests running at once for each schema source, and a test that holds several leases cannot deadlock it.
- **Schema change guard.** Purging rows cannot undo a change to the structure. The server describes each database's schema when the pool builds it (on SQLite, a SHA-256 of `sqlite_master`), and a returned database whose description differs is dropped instead of reused. A change to the migration history alone is not a schema change and the database is reused.

## What the gate found and what was done

- **S1, the reset self-test missed tables.** It filled three tables, so a reset that skipped `Peoples` still passed. Now a Theory over both schema sources fills every model table (checked non-empty first), and after reuse every table count must equal a fresh database's. A second Theory checks that identity restarts at 1 on every identity table. The re-gate confirmed that skipping `Peoples` fails exactly the two reset cases, and that purging the history on the migrated schema fails exactly the Migrations case. A table added later fails the test until the reset handles it.
- **S2, a way around the SQLite ban.** The public `SqliteFileTestBackend` would have let a normal test use SQLite. Its constructor is now in `tests/BannedSymbols.Tests.txt`. Only the pool's own tests use it, under a scoped RS0030 pragma with a reason. The re-gate probed a field initializer, `new SqliteFileTestBackend()` and target-typed `new()`, and all three were caught.
- **S4, a test that changes the schema could poison the pool.** The fingerprint check above. The re-gate saw it fail exactly the four schema cases (CREATE TABLE, CREATE INDEX, CREATE TRIGGER, ALTER TABLE ADD COLUMN) when disabled.
- **Nits folded:** `Lease` and the process-exit `Dispose` share a lock, so a late lease drops its own database; a failed drop raises an `AggregateException` with both failures instead of being swallowed; documentation of per-schema growth and of `DatabaseReset` assuming the default schema; one shared options builder.

## Deferred, with the reason

- **S3, seeding and identity per engine.** The hand-rolled seed insert and SQLite's `sqlite_sequence` delete are right for SQLite only. SQL Server needs `IDENTITY_INSERT` and a careful reseed; MySQL needs `TRUNCATE` or `AUTO_INCREMENT = 1`. These can only be written and tested against those engines, so they are entry criteria for steps 24 and 27 (plan B15), together with running the reset checks against any pooled backend and a fingerprint per engine.
- **The `fresh` flag is not yet proven at the fixture.** The re-gate changed the fixture to pass `false` and every test still passed, because both live backends ignore the flag. A test is only meaningful with a pooled backend behind the fixture, so it is a B15 entry criterion too, along with four small teardown nits.

## Evidence

- Each commit builds alone in Release with 0 warnings and passes the database infrastructure tests; `git diff --check` is clean. Debug warnings are identical to the base list.
- Tests at the branch tip: SQLite Implementations 1610 passed, 85 skipped; Server 85/7; Integration 132/11. PostgreSQL container mode Implementations 1641/51; Server 80/12. The fix round added 7 cases (6 pool self-tests and 1 fixture test) on both providers, with no change in skips.
- The pool self-tests (30 cases) passed 5 runs in a row. The mutation log is `/tmp/step22/mutations-v2.txt`.

## Limits

- No real engine uses the pool yet. SQLite and PostgreSQL keep their template copies, which are faster.
- The pool lives for the test process. If the process is killed hard, databases on an external server can be left behind; the container mode server goes away with its container.
