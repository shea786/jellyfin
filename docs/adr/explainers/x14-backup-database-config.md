# Explainer: X14, keeping the server's database configuration across backups and restores

These are fork-internal study notes, not upstream PR text. They describe the branch and the retained evidence as of 2026-10-17.

## Branch and state

- **Branch:** `multidb/x14-backup-database-config`, cut from the `feature/multi-db` tip `c807e35cff`, in five commits:
  - *Keep the database configuration of the server when restoring a backup* (`2964772ca1`): upstream cherry-pick of `ecdec346ed`. In `BackupService.cs`, `RestoreBackupAsync` skips any entry whose relative path matches `DatabaseConfigurationStore.DatabaseConfigurationFileName` (`database.xml`), case-insensitively (`keep: [DatabaseConfigurationFileName]`).
  - *Leave the database configuration out of backups* (`5a376a16db`): upstream cherry-pick of `0bdd5ea672`. In `BackupService.cs`, `CreateBackupAsync` skips `database.xml` when collecting configuration files to archive.
  - *Pin the database configuration across a database restore on every engine* (`090bdb8136`): adds cross-engine test F1 (`RestoreBackupAsync_WithTheDatabase_KeepsTheDatabaseConfigurationOfThisServer`) in `BackupServiceTests.cs`, and red control 9 proving that an archived `database.xml` cannot overwrite the target host's database configuration.
  - *Say in the how-to that a backup leaves database.xml alone* (`8f7dbaa36c`): updates `docs/database/templates/how-to.md` and re-rendered pages (`postgresql.md`, `mysql.md`, `mariadb.md`).
  - A docs commit with this explainer, the ADR-0001 S1 and ADR-0003 R6 amendments, ADR-0001 Secrets cross-reference, pr-plan B40 with the X14 row, the Group 0 update, and the S9 row.
- **State:** Upstream-accepted PR on `fix/backup-keep-database-config` (`0bdd5ea672`); cherry-picked and augmented with multi-database tests and docs on `multidb/x14-backup-database-config`.
- **Relevant entries:** ADR-0001 S1 (and Secrets :31); ADR-0003 R6; pr-plan B40, S9, Group 0.
- **Evidence:** `/tmp/x14/gates/fork/`.
  - `results.tsv`: per-commit gate matrix results across all 4 commits.
  - `trx-summary.tsv`: audited test results for all 15 legs per commit (60 legs total, 0 failures).
  - `trx-row-audit.tsv`: individual test result audits.
  - `cap.txt`: process caps applied during builds and tests (`MemoryMax=12G`, `MemorySwapMax=0`).

## The gap

`BackupService.CreateBackupAsync` archives files from `ConfigurationDirectoryPath`, and `RestoreBackupAsync` extracts them back into the configuration directory.
Prior to this fix:
1. `CreateBackupAsync` collected all files in `ConfigurationDirectoryPath`, including `database.xml`.
2. `RestoreBackupAsync` extracted all archived configuration files, overwriting the running host's `database.xml`.

This caused severe cross-engine and cross-environment failures:
- **Cross-engine restores:** An operator restoring a backup made on SQLite onto a PostgreSQL, MySQL, MariaDB, or SQL Server instance had their `database.xml` silently overwritten with SQLite configuration. The next server start attempted to read SQLite files or failed unexpectedly while the actual media data remained in the relational database.
- **Cross-environment credential and connection leakage:** Restoring an archive between different servers running the same engine overwrote host connection strings, host credentials, usernames, passwords, and port configurations with those from the source server.
- **Path traversal and casing variations:** Any archive containing paths such as `Config/database.xml` or variations in casing (`DATABASE.XML`) could potentially overwrite the host's configuration unless compared case-insensitively.

## The design

- **Upstream commit 1 (ecdec346ed / 2964772ca1):**
  In `BackupService.cs`, `RestoreBackupAsync` defines a list of configuration files to keep:
  ```csharp
  var keep = new[] { DatabaseConfigurationStore.DatabaseConfigurationFileName };
  ```
  During archive extraction, each file's resolved relative path is checked against `keep` using `StringComparison.OrdinalIgnoreCase`:
  ```csharp
  if (keep.Any(f => string.Equals(f, relativePath, StringComparison.OrdinalIgnoreCase)))
  {
      continue;
  }
  ```
  Any attempt to overwrite `database.xml` from an archive is ignored.

- **Upstream commit 2 (0bdd5ea672 / 5a376a16db):**
  In `BackupService.cs`, `CreateBackupAsync` skips `database.xml` when collecting configuration files:
  ```csharp
  if (string.Equals(Path.GetFileName(file), DatabaseConfigurationStore.DatabaseConfigurationFileName, StringComparison.OrdinalIgnoreCase))
  {
      continue;
  }
  ```
  Newly created archives never carry `database.xml`.

- **Fork cross-engine test F1 (090bdb8136):**
  Cross-engine test F1 in `BackupServiceTests.cs` (`RestoreBackupAsync_WithTheDatabase_KeepsTheDatabaseConfigurationOfThisServer`) verifies on every supported database engine that:
  1. The target server's `database.xml` has specific host configuration (`ThisServerDatabaseConfiguration`).
  2. The archive explicitly injects an entry for `Config/database.xml` containing `ArchivedDatabaseConfiguration`.
  3. Database rows are seeded and modified.
  4. After `RestoreBackupAsync`, the target server's `database.xml` is byte-for-byte identical to what it was before the restore (`Assert.Equal(bytesBefore, File.ReadAllBytes(databaseConfigurationPath))`), and the restored database state is preserved.

- **Red control 9:**
  Proves that without the fix, an archive carrying `Config/database.xml` overwrites the target host's `database.xml`, and the assertion in test F1 fails.

- **How-to documentation (8f7dbaa36c):**
  The documentation in `docs/database/templates/how-to.md` and rendered markdown files (`postgresql.md`, `mysql.md`, `mariadb.md`) explicitly notes that backups leave `database.xml` alone and restores never overwrite it.

## The gate proof

The full multi-database gate matrix ran in detached worktrees under systemd user service `multidb-x14-gate.service` with hard caps (`MemoryMax=12G`, `MemorySwapMax=0`) and file locking (`flock -o /tmp/multidb-heavy.lock`).
Base commit: `c807e35cff77f525a576d5053696428fa9e44bd9` (Debug build: 0 errors, 211 warnings).

Every commit passed all gates:
- **`2964772ca1`**: Debug build 0 errors, 211 warnings (equal to base: yes); Release build 0 warnings, 0 errors; format clean; render check clean. 15 test legs:
  - Sqlite: implementations 71/71 passed, server 33/34 passed (1 skip), integration 3/9 passed (6 skip)
  - Postgres: implementations 70/71 passed (1 skip), server 33/34 passed (1 skip), integration 8/9 passed (1 skip)
  - MySql: implementations 70/71 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - MariaDb: implementations 70/71 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - SqlServer: implementations 60/71 passed (11 skip), server 13/33 passed (20 skip), integration 2/9 passed (7 skip)
- **`5a376a16db`**: Debug build 0 errors, 211 warnings (equal to base: yes); Release build 0 warnings, 0 errors; format clean; render check clean. 15 test legs:
  - Sqlite: implementations 72/72 passed, server 33/34 passed (1 skip), integration 3/9 passed (6 skip)
  - Postgres: implementations 71/72 passed (1 skip), server 33/34 passed (1 skip), integration 8/9 passed (1 skip)
  - MySql: implementations 71/72 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - MariaDb: implementations 71/72 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - SqlServer: implementations 61/72 passed (11 skip), server 13/33 passed (20 skip), integration 2/9 passed (7 skip)
- **`090bdb8136`**: Debug build 0 errors, 211 warnings (equal to base: yes); Release build 0 warnings, 0 errors; format clean; render check clean. 15 test legs:
  - Sqlite: implementations 73/73 passed, server 33/34 passed (1 skip), integration 3/9 passed (6 skip)
  - Postgres: implementations 72/73 passed (1 skip), server 33/34 passed (1 skip), integration 8/9 passed (1 skip)
  - MySql: implementations 72/73 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - MariaDb: implementations 72/73 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - SqlServer: implementations 62/73 passed (11 skip), server 13/33 passed (20 skip), integration 2/9 passed (7 skip)
- **`8f7dbaa36c`**: Debug build 0 errors, 211 warnings (equal to base: yes); Release build 0 warnings, 0 errors; format clean; render check clean. 15 test legs:
  - Sqlite: implementations 73/73 passed, server 33/34 passed (1 skip), integration 3/9 passed (6 skip)
  - Postgres: implementations 72/73 passed (1 skip), server 33/34 passed (1 skip), integration 8/9 passed (1 skip)
  - MySql: implementations 72/73 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - MariaDb: implementations 72/73 passed (1 skip), server 34/34 passed, integration 8/9 passed (1 skip)
  - SqlServer: implementations 62/73 passed (11 skip), server 13/33 passed (20 skip), integration 2/9 passed (7 skip)

Across all 60 test legs: 0 test failures, sentinels verified (`ProviderBackend_IsLive: Passed`, `CreateClient_SelectedProvider_RunsTheServerOnThatProvider: Passed`), and all SQL Server skipped tests verified with explicit skip reasons (`SchemaMigrationsFactAttribute`).
