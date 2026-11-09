# Explainer: step 21, guard against unfinished schema migrations

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/21-non-transactional-migration-guard`, three commits on the `feature/multi-db` tip after step 11 (`fccca748c7`).
  - `a0379e3c20` Add SupportsTransactionalMigrations to the database provider
  - `20e6082746` Record the running schema migration for providers without transactions
  - `ac8588728f` Refuse to migrate while a schema migration is marked unfinished
- **Status:** gated (the gate asked for fixes to the marker handling, all folded) and merged into `feature/multi-db`. Not pushed. Backups of earlier versions: `multidb/21-backup-a` to `-e` and `backup/step21-before-sa1201-fix`.

## Why it exists

MySQL and MariaDB commit every DDL statement implicitly. If a schema migration fails half way, nothing is rolled back: some tables or columns exist, and `__EFMigrationsHistory` has no row for the migration. The next start sees the migration as pending and runs it again, which fails on objects that already exist, or worse, succeeds against a schema it does not fit. SQLite and PostgreSQL run a migration in a transaction, so a failure leaves the database as it was. This step adds the guard before any MySQL or MariaDB provider exists (steps 24 and 25), so the provider has something to rely on.

## What changes, commit by commit

- **Provider flag.** `IJellyfinDatabaseProvider.SupportsTransactionalMigrations` is a default interface member returning `true`. Existing providers and plugins need no change. A MySQL provider will return `false`. The interface is in `Jellyfin.Database.Implementations`, which is on the ABI list, so the member is permanent once published.
- **Marker file.** For a provider that returns `false`, the server writes `migration-in-progress.json` into the data directory before each schema migration: the migration id, the provider key and the start time. It deletes the file once the history contains that migration. If the migration throws, the Critical log gives the full path and says to restore the database from the backup taken before the upgrade and delete the file, then the exception propagates and the file stays. A file that cannot be written stops the migration before it starts (a partly written file is removed). A file that cannot be deleted after the migration was applied stops startup with a message saying exactly that, and nothing is rolled back, because the database is in the state the migration intended.
- **Refusal.** On the next start, if the file exists, the server logs Critical and throws before anything is migrated. The check runs at two entry points: `CheckFirstTimeRunOrMigration`, which is also the gate for `--restore-archive`, and `MigrateStepAsync` for the Core stage. It runs for every provider, including upstream SQLite, so a marker left by a previous configuration is not ignored after the provider is changed. It does nothing and logs nothing when there is no file.

## The rules a reader will ask about

- **Order on failure:** backup (taken earlier by the existing flow), marker written, migration fails, Critical log, rollback where the engine supports it, rethrow, marker stays.
- **Self-heal.** The history row is the last statement of a migration. If the marker names a migration of the configured provider and the history already contains it, the migration finished and only the delete was lost (for example a crash between the two). The server deletes the marker with a Warning and continues. If that delete fails it refuses with the specific message. It still refuses when the provider differs, when the file cannot be read, and when the migration is not in the history. If the database or history table does not exist the answer is "not applied" and nothing is created. Any other failure while checking logs a Warning and falls back to refusing.
- **`--restore-archive`.** A Jellyfin backup archive cannot repair a partly migrated schema, so the refusal message says to restore the database dump, not the archive.
- **Code routines are not covered.** They contain no schema changes and are not atomic with their history row on any provider.

## Evidence

- Every commit builds alone in Release with 0 warnings; `git diff --check` is clean. Debug warnings are identical to the base list.
- Tests at the tip: SQLite Implementations 1577 passed, 85 skipped; Server 114 passed, 7 skipped; Integration 132 passed, 11 skipped. PostgreSQL container mode: Implementations 1608 passed, 51 skipped; Server 109 passed, 12 skipped. Server grew by 14 on both providers, which is the 14 marker tests added in the fix round (29 in total). Implementations and Integration are unchanged.
- Each behaviour change in the fix round was seen red before green. Two PostgreSQL failures on the first run were test bugs: Npgsql's `IHistoryRepository.ExistsAsync` always returns true, so the test now uses `IRelationalDatabaseCreator.HasTablesAsync`; and PostgreSQL has one schema migration, so after the first it has nothing pending and the self-heal test no longer asserts a true return.

## Limits and follow-ups

- No shipped provider returns `false` yet, so no real engine exercises the guard. The marker tests drive it with a test provider.
- `BackupService` (around line 214) calls `MigrateAsync` outside this service, so a restore path can migrate without the marker. Step 24 must route it through the same logic or refuse, and must make the pre-migration backup real (plan amendment B14).
- The marker is only as good as the backup the operator restores from. Nothing here creates that backup.
