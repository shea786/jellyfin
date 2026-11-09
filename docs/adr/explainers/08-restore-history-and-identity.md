# Explainer: step 08, restore a backup safely and onto any engine

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/08-restore-history-and-identity`, seven commits on `a741bc6035`.
  - `2ba233b984` Pin the SQLite restore round trip in tests
  - `0c2a3eeae9` Validate a backup before replacing the database
  - `bb4cf39891` Keep the target's migration history when restoring
  - `6ed8b66ecc` Restore the generated ids of a backup
  - `728aa62363` Let providers prepare explicit identity inserts and finish a restore
  - `5f33d497db` Remove NUL characters from restored rows, keys included
  - `b027299e73` Create the schema before restoring a backup into a new instance
- **Status:** gated (three rounds) and merged into `feature/multi-db`. Not pushed. Backups of earlier versions: `multidb/08-backup-a`, `-b`, `-c`.
- **Closes Group 0.** Steps 01 to 08 are the fixes that make sense for SQLite users on their own and that every other engine builds on.

## Why it exists

Upstream's full-system restore (`BackupService`) had four problems that matter today on SQLite and become blockers on any other engine:

1. It deleted every table first and only then read the archive, so a damaged archive left an empty database.
2. It replaced the migration history with the archive's rows, so a backup from an older server made the next start replay migrations against tables that already had them.
3. It could not read back generated integer ids (private setters are skipped by the JSON serializer), so ids changed on every restore.
4. On a brand-new server, `--restore-archive` failed with "no such table" (upstream issue #17449).

And for other engines: a SQLite backup can hold NUL characters that PostgreSQL rejects, engines with sequences or per-session identity rules need hooks, and tables must be inserted in foreign-key order.

## What each commit does

1. **Pin the round trip.** Tests only. Records today's behaviour, including the id drift.
2. **Validate first, then one transaction.** Every table entry is deserialised once before anything is touched; the history rewrite, purge and inserts run in one transaction. SQLite ignores `PRAGMA foreign_keys` inside a transaction, so the purge uses `PRAGMA defer_foreign_keys`. Side effect: cascades now fire during the purge, which changes nothing because every table is deleted anyway. This overlaps open upstream PR #17899; if #17899 lands, this commit is replaced by it on rebase and commits 3 to 7 are adjusted (they build on this commit's transaction and validation, so it cannot simply be dropped).
3. **Keep the target's history.** Schema migration rows of the target stay. Only code routine rows are taken from the archive, and only those whose id the server knows, through `IMigrationRoutineCatalog` (public, in `Jellyfin.Server.Implementations`, which is not on the ABI list; ADR-0003 R2 explains why it must be public). A schema migration id from another engine is never copied, because it could make the migrator skip a later migration with the same id.
4. **Restore generated ids.** Ids are set through EF after reading, and checked during validation, so `"Id":"not-a-number"` rejects the archive instead of silently dropping the row. SQLite's `sqlite_sequence` counters are reset on purge, so ids no longer drift upward.
5. **Provider hooks.** `BeginExplicitIdentityInsertAsync(dbContext, ...)` returns a per-table scope (SQL Server allows `IDENTITY_INSERT` for one table at a time) and `CompleteDatabaseRestoreAsync(dbContext, cancellationToken)` runs once, inside the transaction, after the last table. The second has exactly the signature of open upstream PR #17901. Both default to doing nothing, so SQLite is unchanged. The restore now saves per table, in foreign-key order, and clears the change tracker after each table so memory is bounded by the largest table.
6. **Clean NUL on restore, keys included.** Every string is cleaned as it is read. If cleaning makes two rows equal in a key or unique index, the row that needed no cleaning wins (the NUL copy is the stale one: display preferences `web` beat `web\0` in either order). References to dropped rows are re-pointed, including `ActivityLogs.UserId`, which has no foreign key. Per-user folders on disk are not touched. Known limit: a same-table reference to an evicted row is not re-pointed; that needs a self-referencing table with a unique string index, which the model does not have, and a guard test fails if one is ever added.
7. **Migrate before restoring.** After validation, the server applies its own schema migrations, so a restore into a never-started instance works (#17449). On an up-to-date server this does nothing.

## What changes for a SQLite user

- A damaged backup no longer destroys the current database.
- Restored rows keep their original ids, and repeated restores no longer push ids upward.
- Restored text no longer contains NUL characters.
- `--restore-archive` works on a fresh install.
- Archive rows for routines this server no longer has are not copied (they would never run anyway).

## Cross-engine evidence

In a throwaway combination with the PostgreSQL provider (step 15), a SQLite backup restored onto a fresh `postgres:16.15-trixie` end to end: empty database, restore over existing data, foreign-key order, NUL removal, timestamps, Guids, target history kept. One gap remains for PostgreSQL itself: identity sequences must be moved past the restored ids with `setval`, otherwise the next insert fails with 23505. That is the PostgreSQL provider's `CompleteDatabaseRestoreAsync`, carried in the PostgreSQL stack (ADR-0003 R3).

## Evidence

- Each commit builds alone (Release 0 warnings, Debug the same 211 warnings as the base). Tip: Implementations 1377 passed / 12 skipped, Server 54, Integration 129 / 3.
- A real server ran backup create and restore over HTTP, kept the access token, and restored a backup into a brand-new instance with `--restore-archive`.
- Mutations caught: the cleaner, the foreign-key re-pointing, the #17449 repro, the routine filter, the clean-row preference (14 tests fail when first-row-wins is forced), and the id validation.
- Reviewers tried 19 adversarial collision cases (three-way, eight orders of users with activity logs, child re-pointing); all pass.

## Upstream overlap

- #17899 (open): replaces commit 2 on rebase if it lands.
- #17901 (open, depends on #17899): same `CompleteDatabaseRestoreAsync` member; ours merges into it.
- #17449 (open issue): fixed by commit 7.

## Limits

- `MigrateAsync` runs before the transaction, so a failed restore leaves the schema upgraded, which the next start would have done anyway.
- Archive validation reads every table twice (once to validate, once to restore): slower on large libraries, flat memory.
- `HomeSection` is not a `DbSet` and is never backed up. That is an upstream gap, not changed here.
