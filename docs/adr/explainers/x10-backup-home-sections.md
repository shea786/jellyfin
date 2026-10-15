# Explainer: X10, backing up and restoring the home sections

These are fork-internal study notes, not upstream PR text. They describe the branches and the retained proof as of 2026-10-15.

## Branches and state

**Upstream-shaped:** `fix/backup-home-sections`, one commit `2084aa42b2` on `aec42b345e`. Its backup, `-backup-a`, is `9f4da47963`.

**Fork:** `multidb/x10-merge-candidate`, one commit `939887782c` on `3d0ab14734`, tree `c2591c7402`.
- **Backup.** `-backup-a` is `8ef5f18861`, the same tree with the earlier commit message. `/tmp/x10/B-reword/` records the reword: tree, parent and identity unchanged, 0 bytes of diff.
- **Single commit.** The fork commit is a single commit, not a merge, because the fork version differs from the upstream one (see below).

**State.**
- The orchestrator fast-forwarded `feature/multi-db` to `939887782c`.
- It was not pushed at the time of writing; the coordinator pushes it.
- No CI result at `939887782c` is claimed.

**Relevant entries:** pr-plan S9 (X10), B28 (22d finding F1) and B29. **Evidence:** `/tmp/x10/`.

## The bug

A backup carries only the tables that have a `DbSet` on `JellyfinDbContext`, and a restore restores only those. `HomeSection` had no set, so a backup never contained the home-screen sections. What happened to them on restore depends on the engine:
- **SQLite and PostgreSQL.** The restore's purge of `DisplayPreferences` cascaded and deleted the sections.
- **MySQL and MariaDB.** The provider purges with `SET SESSION foreign_key_checks = 0` (`MySqlDatabaseProvider.cs:407`), so the cascade never fired. The sections survived the purge, and the restore left them as they were after the backup, possibly pointing at display preferences that no longer exist.

Either way, a restore did not give back the sections of the home screen. The 22d conformance kit found this (22d finding F1). Its restore row listed `HomeSection` as "not in the backup", an exception written to fail once a restore gives the rows back.

## The upstream-shaped fix (`2084aa42b2`)

**A new set: `public DbSet<HomeSection> HomeSection`, singular.**
- **The name follows the table.** A set gives its name to its table, so `HomeSections` would rename the table and need a migration.
- **Backup and restore must agree.** The backup names each archive entry after the table, and the restore looks the entry up by the name of the set. If the two differ, the restore silently skips the entry.

**The restore keeps the primary keys from the archive.**
- **Why.** `System.Text.Json` does not set private setters, so restored `DisplayPreferences` rows got new ids, and the sections' foreign keys pointed at nothing.
- **Order.** With only the new set, the restore failed on a foreign key after the tables were already emptied.
- **Pre-purge read.** The keys are now read and checked before the purge, so an archive with an unreadable key fails while the database is still intact.
- **Keyless sets.** A set with no primary key is guarded (`FindPrimaryKey()?.Properties ?? []`); the model has none today.

**Caveat in the commit body.** The restore now inserts explicit identity values.
- On SQLite this is correct: SQLite raises `sqlite_sequence`.
- Upstream has nothing for the other engines: SQL Server refuses explicit identity values without `IDENTITY_INSERT`, and PostgreSQL would leave its sequences behind, so later inserts would collide.

**Tests.** `CreateBackupService` now purges with the SQLite provider instead of a mock that did nothing, so restore tests really restore. Two new tests:
- `RestoreBackupAsync_WithHomeSections_RestoresThem`;
- `RestoreBackupAsync_WithDamagedKey_LeavesDatabaseUntouched`.

## The fork version (`939887782c`)

**Smaller than upstream.** The fork's `BackupService` already:
- reads the generated keys before the purge and keeps them;
- saves each table after the tables it refers to;
- moves identity counters past the restored ids through the provider hooks.

So the fork commit adds only the set and the test (`JellyfinDbContext.cs`, `BackupServiceTests.cs`), and updates the kit.

**The kit.** `RestoreConformance`'s not-in-the-backup list is removed; `HomeSection` was its only entry. The restore row now compares the sections like every other table, including the identity check. The concurrency-token (RowVersion) exception remains, and still expires on its own when a restore keeps the tokens.

**The reword.** The first fork commit message said the sections were "deleted on every engine". That is wrong for MySQL and MariaDB, so `8ef5f18861` was reworded to `939887782c`, with no change to the tree.

## Failing first

**Upstream shape** (`/tmp/x10/A-gates/results.tsv`):
- **Base with the tests only:** both new tests fail. The third test in the filter, `CheckForUnappliedMigrations_SqLite`, passes.
- **No-op-restore mutation:** both new tests fail.
- **No-pre-purge-read mutation:** only `WithDamagedKey` fails, as expected.

**Fork** (`/tmp/x10/B-red/results.tsv`):
- **Base with the test only:** `WithHomeSections_RestoresThem` fails on all four engines.
- **Kept-gap kit row:** with the kit's exception kept, the restore row fails on SQLite and PostgreSQL with "HomeSection: the restore now gives rows back, remove the table from the tables the backup does not carry".

## Gates and proof

**Upstream shape at `2084aa42b2`** (`/tmp/x10/A-gates/`, `COMPLETE`, rc 0):
- Release 0 warnings and 0 errors.
- Format rc 0; the base is rc 0 too.
- SQLite Implementations 1090 run, 1078 passed, 0 failed, 12 skipped.
- Server.Tests 39/39.

The superseded run on `9f4da47963` is kept as `A-gates.superseded-9f4da47963/`.

**Fork** (`/tmp/x10/fork-proof/`, `COMPLETE`, 14/14 gates rc 0, Release 0/0, format 0). The run was made at `8ef5f18861`, which has the same tree as `939887782c`.

| Backend | Implementations total/passed/skipped | Server.Tests total/passed/skipped | Integration total/passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2574/2462/112 | 166/157/9 | 148/137/11 |
| PostgreSQL 16.15 | 2570/2496/74 | 165/147/18 | 150/144/6 |
| MySQL 8.4.11 | 2528/2371/157 | 165/127/38 | 150/144/6 |
| MariaDB 11.4.13 | 2528/2371/157 | 165/127/38 | 150/144/6 |

- **TRX rows.** 12 TRX files, 11,459 rows: 10,827 Passed, 632 NotExecuted, none failed. I recounted them, and the orchestrator audited them independently.
- **Sentinels.** All three pass on every engine.
- **New tests.** `fork-proof-rowcheck.txt` shows the new test passing on all four engines, and the kit's restore and identity rows passing on SQLite and PostgreSQL.
- **Identity.** The start and end identities are exact, with empty status files.
- **Docker.** The added and remaining-new-Testcontainers lists are empty. The removed list has one Testcontainers Ryuk container (session `8ec0ff35`) that existed before the run.
- **Debug** (`/tmp/x10/fork-debug/`). 211 against 211, empty diff, base `3d0ab14734` against `8ef5f18861`.
- **After the reword** (`/tmp/x10/B-reword/check/`). The tree is identical. At `939887782c`, Release is 0/0 and `BackupServiceTests` passes 65/65 on SQLite.

## Next ids after a restore

This was checked in a scratch test that was never committed (`/tmp/x10/identity-scratch/v3/`).
- **With the provider's completion hook.** On all four engines, the next `DisplayPreferences` and `HomeSection` ids after a restore were above the restored maximum ("section restored max 2 new 3; preferences restored 2 new 3").
- **Without the hook.** PostgreSQL collided (`23505: duplicate key value violates unique constraint`). SQLite, MySQL and MariaDB do not need the hook for this.

## Backup compatibility

The backup engine version stays 0.2.0.
- **Old archive, new server.** An archive made before X10 has no `HomeSection` entry, which the restore skips like any missing table, so it restores as before, without sections.
- **New archive, old server.** A server without X10 ignores the entry and restores the archive without the sections, without error.
- **Why not bump.** A version bump would make the servers refuse each other's archives.

## Review

Relayed by the coordinator; the review notes are not in the evidence.
- **First review: APPROVE**, with S1 (the commit body), test hardening and the pre-purge read.
- **Re-review: APPROVE** on both shapes.

## Notes and limits

- **Upstream order of restore.** Upstream restores Config and Data before a database restore that it may then refuse. That is pre-existing; the fork already checks first. It is an upstream follow-up.
- **Every archive entry is now read twice**, both times streamed.
- **MySQL and MariaDB coverage.** The kit rows do not run on MySQL or MariaDB, so the `HomeSection` identity after a restore there has no committed coverage. `BackupServiceTests` covers the restore itself on those engines, and the next-id check was scratch only. That is for 27a.
- **A harmless `!`.** The fork's `GetGeneratedKeys` still uses `FindPrimaryKey()!` (`BackupService.cs:421`).
- **Backups made before X10 still lack the sections.** Restoring them cannot bring sections back.
