# Explainer: step 07, retry a write that lost a race

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/07-write-conflict-retry`, five commits on the Group 0 tip at the time (`a741bc6035`).
  - `58819008e7` Pin that a stale save of a user raises a concurrency error (test only)
  - `a06b450cc1` Add a provider hook that classifies retryable write conflicts
  - `556c1bba88` Add a bounded retry for write conflicts
  - `619433b3b0` Retry reattaching user data on a write conflict
  - `8b61826d1f` Retry saving items on a write conflict
- **Status:** gated and merged into `feature/multi-db`. Not pushed. Backup of the earlier four-commit version: `multidb/07-backup-a`.

## Why it exists

Two writers can both read "this row does not exist", and both insert it. On an engine with concurrent writers the second insert fails on a unique or primary key, or the engine picks it as a deadlock victim. Running the whole write again would succeed, because the second run reads the row the winner inserted. Shared code cannot tell such a failure from a real bug, because the error codes belong to the engine (ADR-0008).

## What changes

- `IJellyfinDatabaseProvider.IsRetryableWriteConflict(Exception)` is a new member with a default of `false`, so a provider that does not implement it behaves exactly as before. This is a public addition to `Jellyfin.Database.Implementations`, which the upstream ABI check covers, so it is permanent once released.
- The SQLite provider answers `true` for a `SqliteException` with extended code 2067 (unique) or 1555 (primary key), also when wrapped in a `DbUpdateException`. BUSY and LOCKED are left out on purpose: the busy timeout and `OptimisticLockBehavior` already retry the command, and BUSY never follows a stale read.
- `WriteConflictRetry` (internal to `Jellyfin.Server.Implementations`, so not part of the ABI list) runs a unit of work up to 3 times, with a random 50 to 250 ms pause, only when the provider says the exception is retryable. The last failure and every non-retryable failure are rethrown unchanged. The log line carries the attempt number and the exception type, not the message, because the message can contain the conflicting values.
- Two callers use it: `ReattachUserDataAsync` and the item save in `ItemPersistenceService`. Each attempt creates its own context and transaction and reads again, because a context that failed to save still tracks the rejected rows.
- A new first commit pins existing behaviour: a stale save of a `User` still throws `DbUpdateConcurrencyException` and bumps the row version by one. A stale save of an existing row is not curable by running it again, so it is deliberately not retried.

## What it does on SQLite

Nothing at runtime. A SQLite transaction takes the write lock when it begins, so there is no window between the read and the insert and the conflict cannot happen. The retry never runs there. The tests stage the race on the attempt's own connection (an interceptor inserts the winning row right after the read), so they are valid on SQLite and prove the retry logic itself. The value of the step shows on PostgreSQL, MySQL, MariaDB and SQL Server, where each provider adds its own classification (PostgreSQL 23505, 40001, 40P01 comes with the PostgreSQL provider rebase).

## Evidence

- Each commit builds and passes alone: Release 0 warnings, Debug 211 warnings with the same sorted list as the base. Test counts per commit (Implementations / Server / Integration): 1342, 1354, 1368, 1369, 1370 / 52 / 129.
- The cancellation test was mutation-checked: replacing the cancellable wait with `Thread.Sleep` makes it fail after the full minute.
- The two caller tests fail without the retry and pass with it.

## Upstream overlap

Open PR #17119 touches the item value race in `ItemPersistenceService` with its own test (`ItemPersistenceServiceItemValueRaceTests.cs`). Commit 5 is the part that overlaps. If #17119 merges, drop commit 5 and fix commit 4 so that test still builds. Commits 1 to 4 do not depend on it.

## Limits

- The retry covers two units of work. Other read-then-insert paths are not wrapped; they are listed as follow-ups once a provider shows them failing.
- Three attempts is a judgement, not a measurement. It can be tuned in one constant.
