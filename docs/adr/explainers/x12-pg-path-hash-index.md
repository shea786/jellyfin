# Explainer: X12, a hash index for item paths on PostgreSQL

These are fork-internal study notes, not upstream PR text. They describe the branch and the retained evidence as of 2026-10-16.

## Branch and state

- **Branch:** `multidb/x12-pg-path-key`, cut from the `feature/multi-db` tip `93c7ffec57`.
  - *Index item paths with a hash index on PostgreSQL*: the code, the migration and the tests.
  - A docs commit with this explainer, the ADR-0009 L8/L9 and ADR-0012 T6 amendments, pr-plan B36 with the X12 row, and the upgrade note of the PostgreSQL how-to.
- **State.** Fork-only. The fix lives on `multidb/x12-pg-path-key`; the orchestrator reviews it and merges it into `feature/multi-db`. The first round, before the review fixes, is kept as `multidb/x12-pg-path-key-backup-a` (`3318c797f0`, on `bbe7c929d9`).
- **Relevant entries:** ADR-0009 G1, L6, L7, L8 and L9; ADR-0012 decision 8, T5 and T6; pr-plan B34, B35 and B36.
- **Evidence:** `/tmp/x12/`.
  - `evidence/options/`: the option probe on PostgreSQL 16.15.
  - `evidence/btree-compression.txt`.
  - `evidence/mysql/`: the `SHOW INDEX` probe.
  - `red2/legs/`: the red-first legs.
  - `dev/`: the development runs.
  - `superseded/`: the stalled and broken runs, with a README.
  - `gates/`: the per-commit gates (`percommit/`), the Debug comparison with the base (`debug/`) and the tip proof (`proof/`). The results are under "Gates" below.

## The gap

PostgreSQL refuses a btree index entry over 2704 bytes (`54000`). Before X12, `IX_BaseItems_Path` was a btree. An item whose path did not compress to that size was therefore never saved on PostgreSQL, a Supported engine, while SQLite saved it (ADR-0009 G1).

X11 limited the damage to that one item, and pinned the gap with the kit row `SaveItemsSkippingFailures_OneItemWithAPathOverTheIndexKeyLimit_SavesEveryItem`, marked `PG-PATH-INDEX-KEY`.

**Context, not a correction of the gap's size.**
- PostgreSQL compresses an index entry before it checks the limit.
- A repetitive 4000-byte path, with the same folder names over and over, was stored in 124 bytes and already saved before X12.
- Values that do not compress failed:
  - random letters at 2700 characters, with a 2712-byte entry;
  - random CJK at 1000 characters, with a 3016-byte entry.
- Real deep paths with unique long names, or with non-Latin names, sit in between. The gap was real for any of them that did not compress enough.

## How the path is queried

I checked every query on `BaseItems.Path` in shared code, and captured the SQL from EF's real calls rather than reading the LINQ.

| Shape | Where | SQL on PostgreSQL |
|---|---|---|
| Equality | `BaseItemRepository.TranslateQuery.cs:327-331`, used by `LibraryManager.FindByPath` (11 callers) | `"Path" = @pathToQuery`, with `IsFolder`, `ORDER BY DateCreated DESC`, `LIMIT` |
| Membership | `ItemPersistenceService.cs:637-642` (children known by path) and `:723-728` (local alternate versions) | `"Path" IS NOT NULL AND "Path" = ANY (@p) ORDER BY "Path" NULLS FIRST` |
| Routines | `FixIncorrectOwnerIdRelationships.cs:71-77` and `:104-113`; `MigrateLinkedChildren.cs:78-82`; `RestorePlaylistChildrenFromMetadata.cs:58-62` | whole-table `GROUP BY "Path"` and `ORDER BY "Path"`; equality |
| Not indexable | `IS NOT NULL` and `<> ''` filters in the routines | – |
| In memory | `StartsWith` in `FixIncorrectOwnerIdRelationships.cs:266`, `GetFileName` in `ConsolidateLocalizedUserViews.cs:89` | not SQL |

Two of these shapes are easy to miss:
- **The ORDER BY in the membership query.** `.GroupBy(e => e.Path!).ToDictionary(...)` is a *final* GroupBy, which EF Core translates as an `ORDER BY` on the key and groups on the client. So a query that looks like a plain lookup asks for order.
- **The whole-table routines.** They look as if they could use the btree. The planner did not use it: it chose a Seq Scan with an external sort on both indexes.

Nothing sends a prefix, `LIKE`, a range or `IS NULL` on `Path`, and no sort key orders by it.

## The options, measured

The probe (`evidence/options/probe.txt`, with a summary in `plans-summary.txt`) ran on PostgreSQL 16.15, the CI image, with 300,001 items and an average path of 88 bytes.
- It called the server's own code: `BaseItemRepository.GetItemList` and `GetItemIdsList`, and `ItemPersistenceService.SaveItems` with a playlist and a video.
- It copied the routines' LINQ.
- An interceptor kept each command with its parameters, and the probe ran `EXPLAIN (ANALYZE, BUFFERS)` on exactly that command.
- It also ran the plan a prepared statement would get, with `plan_cache_mode = force_generic_plan`.

| | btree (before) | **hash** | btree on `md5("Path")` | partial btree |
|---|---|---|---|---|
| 3024 B and 10,000 B paths | `54000` | saved, read back, found by `FindByPath` | saved | saved |
| `FindByPath` | Index Scan, 0.09 ms | Index Scan, 0.08 ms | Seq Scan, 650 ms | Seq Scan, 645 ms |
| `= ANY` + order | ordered Index Scan, 0.03 ms | Bitmap Index Scan + quicksort of 5 rows, 0.02 ms | Parallel Seq Scan, 20 ms | Parallel Seq Scan, 21 ms |
| Routines | Seq Scan + sort | unchanged | unchanged | unchanged |
| Size; build (3 rounds) | 35 MB; 138-154 ms | 8.2 MB; 163-175 ms | 17 MB; 157-164 ms | 35 MB; 145-148 ms |

- **The md5 and partial indexes need the query changed.** They reach an index scan (0.14 ms) only once the predicate is rewritten: `md5("Path") = md5(@p)` beside the equality, or the partial index's condition repeated. That is a shared-query change, or a provider query interceptor. 22d's neutrality rule argues against both.
- **The truncated-expression variant** has the same need.
- **`hashtext`** is internal and undocumented, so it was not measured.

## What was built

- **`PathHashIndexConvention`** (PostgreSQL provider) gives the shared `IX_BaseItems_Path` the method `hash`.
  - It throws if the model has no index on `Path` alone, so a change to the shared index cannot silently leave PostgreSQL with something else.
  - It is registered before `NullsFirstIndexConvention`.
  - It also refuses a unique index, which a hash index cannot be. A filter PostgreSQL would accept, so the convention leaves that to the test, which pins that there is none.
- **`NullsFirstIndexConvention`** now skips any index that is not a btree.
  - Without this, it gave the nullable `Path` `NULLS FIRST`, which PostgreSQL refuses on a hash index.
  - The method is compared without regard to case. Every other index keeps its null order. The PostgreSQL snapshot changes on one line, `HasNullSortOrder(NullsFirst)` becoming `HasMethod("hash")` on `Path`.
  - The existing test that every nullable index column sorts nulls first now looks at btree indexes only.
- **The migration** `20261016043615_IndexItemPathByHash` was generated with `dev/add-migration`.
  - PostgreSQL: `DropIndex` then `CreateIndex ... USING hash`. Down restores the btree with nulls first, and fails while a path too long for a btree entry exists.
  - SQLite and MySQL: the same id with no operations, and a comment saying why. The 22b pairing theory requires every SQLite id after a baseline on every provider. The MySQL baseline covers through `20261013120000`, so the empty SQLite migration forces an empty MySQL one.
  - The tool writes a byte-order mark into every file it creates or rewrites. The committed migration files and the PostgreSQL and MySQL snapshots have none, so it was stripped from them. The SQLite snapshot already had one and the tool rewrote it byte for byte, so it was left as it is. With that, the SQLite and MySQL snapshots are byte-identical to the base.
  - Four `EngineSpecific` entries in the 22c allowlist record PostgreSQL's operations in Up and Down.
- **Tests.**
  - The known gap and its two matcher tests are gone. The 3000-character kit row runs plainly on every engine.
  - A new kit row saves a 10,000-byte path with `SaveItems` and finds the item by equality and by `Contains`.
  - `PostgresPathIndexTests`:
    - the index is a hash in the model and in the database, not unique and not filtered;
    - it is the only index that is not a btree, so another one becomes a deliberate decision;
    - every lookup by path the server sends (the equality and the two `= ANY` queries, captured with their parameters) can be answered from it. That test turns sequential scans off, so it shows only that the index can answer each lookup, which a btree could as well. The proof that the index is a hash index is the first test.
  - `IndexItemPathByHashMigrationTests` in Server.Tests:
    - on every engine, a database of the previous migration keeps its items and finds them by path, and a long path saves after the migration;
    - on PostgreSQL, the btree refuses a long path with `54000` before the migration, the index is a hash after it, and Down brings the btree back;
    - the generated scripts, up and back down, change the index on PostgreSQL only. Down drops the hash index and then creates `("Path" NULLS FIRST)` with the default btree method.

## Red first

The legs are in `/tmp/x12/red2/legs/`, run on the code commit `6d5f5f0d92` and finished with `COMPLETE`. Every dotnet process ran under a 12G memory cap.

| Leg | Result |
|---|---|
| (a) the base with the fix's kit rows, marker removed | PostgreSQL fails the 3000-character row with `54000` "index row size 3024 exceeds btree version 4 maximum 2704 for index IX_BaseItems_Path"; SQLite, MySQL and MariaDB pass |
| (d) the new 10,000-byte row at the base | PostgreSQL fails with `54000` "index row requires 10016 bytes, maximum size is 8191" |
| (b) the fix with the marker put back | PostgreSQL fails with "[known gap PG-PATH-INDEX-KEY: …] The test passes on Postgres, remove the KnownGap marker."; the others pass |
| (c) the fix | all four engines pass |
| An allowlist entry removed | the contents check reports `Missing: 1`, the `CreateIndex BaseItems (Path)` of Up; restored, it passes |
| (f) the MySQL prefix limit set to 511 | `PathIndex_IndexesAPrefixOf512Characters` fails on all five rows (MySQL 8.4 and 9.7, MariaDB 11.4.7, 11.8 and 12.3) with "Expected: [512] Actual: [511]"; with the reverse patch applied, all five pass |

## Gates

The gates are in `/tmp/x12/gates/`, a link to `/tmp/x12/gates2/`. They were run on the code commit `6d5f5f0d92` and on the first version of the docs commit, `c24fd58c40`, under one lock hold with a 12G memory cap, and each finished with `COMPLETE`.
- **Per commit** (`percommit/`), for both commits:
  - Release `--no-incremental` gave 0 warnings and 0 errors;
  - the exact format command and the render check returned 0;
  - the targeted tests passed on all four engines, with every backend sentinel passing:

    | Engine | Implementations | Server.Tests | Integration |
    |---|---|---|---|
    | SQLite | 729 passed, 90 skipped | 127 passed, 11 skipped | 3 passed, 6 skipped |
    | PostgreSQL | 787 passed, 42 skipped | 119 passed, 18 skipped | 8 passed, 1 skipped |
    | MySQL | 714 passed, 103 skipped | 97 passed, 40 skipped | 8 passed, 1 skipped |
    | MariaDB | 714 passed, 103 skipped | 97 passed, 40 skipped | 8 passed, 1 skipped |

- **Debug** (`debug/`). Debug `--no-incremental` of the base and of both commits gave 0 errors and the same 211 normalised warnings, with empty diffs.
- **The tip proof** (`proof/`) on `c24fd58c40`:
  - Release gave 0 warnings and 0 errors, and format returned 0.
  - The full suites:

    | Engine | Implementations | Server.Tests | Integration |
    |---|---|---|---|
    | SQLite 3.53.3 | 2686 passed, 126 skipped | 161 passed, 11 skipped | 137 passed, 11 skipped |
    | PostgreSQL 16.15 | 2684 passed, 106 skipped | 153 passed, 18 skipped | 144 passed, 6 skipped |
    | MySQL 8.4.11 | 2629 passed, 159 skipped | 131 passed, 40 skipped | 144 passed, 6 skipped |
    | MariaDB 11.4.13 | 2630 passed, 158 skipped | 131 passed, 40 skipped | 144 passed, 6 skipped |

  - Controller.Tests: 218 passed.
  - The fail-closed TRX audit read 12 files with 12,461 rows: 11,774 passed, 687 skipped, none failed. All three sentinels passed in every leg.
  - On every engine, the pending-changes theory passed for all seven provider rows, the pairing theory and the migration contents check passed, and `PostgresPathIndexTests` passed on PostgreSQL.
  - Docker by label: nothing added or removed, and no Testcontainers identity left behind.
  - The start and end identity are `c24fd58c40`, with a clean status.
- **The docs commit was then amended**, docs only, to quote these results. Its tree differs from `c24fd58c40` only under `docs/`, so the runs above hold for it.

## MySQL and MariaDB

This section is documentation only. The probe ran at the base on MySQL 8.4.11 and MariaDB 11.4.13; the output is in `evidence/mysql/`.
- `SHOW INDEX` gives `IX_BaseItems_Path` an explicit `Sub_part` of 512 with `BTREE`.
- The three-column series composite gets 170 per column.
- No index of `BaseItems`, `Peoples`, `ItemValues`, `BaseItemProviders` or `MediaStreamInfos` is `HASH`.
- 909, 3024 and 10,000 characters each saved 50 of 50 in one batch, and equality found the item.

That matches ADR-0009 L6. The prefix is pinned by tests:
- **Offline.** `MySqlModelTests.PathIndex_IndexesAPrefixOf512Characters` asserts the 512 prefix-length annotation on `IX_BaseItems_Path` for every MySQL and MariaDB row of the provider table (8.4, 9.7, 11.4.7, 11.8 and 12.3). With the convention's limit changed to 511 in a scratch edit, it fails.
- **On a server.** `MySqlBaselineSchemaTests.MigratedDatabase_IndexesTextOnlyByThePrefixTheConventionChose` already read `SUB_PART` from `information_schema.STATISTICS`, the source of `SHOW INDEX`, and compared every index with the model. It now also asserts 512 for `IX_BaseItems_Path`. `MigratedSchema_MatchesTheSchemaOfTheModel` compares `INDEX_TYPE` with the model's.
- **Correction.** The first round of these notes said that no test reads `SHOW INDEX`, and the request for a pin rested on that. That was wrong: these two server tests existed, and `CreateScript_UsesTheColumnTypesOfTheModel` already checked `` `Path`(512) `` in the script for 8.4 and 11.4.13.

## The other text-class columns

These are recorded in ADR-0009 L9 and not fixed here.
- **The single-column indexes** on `Name`, `PresentationUniqueKey`, `SeriesName`, `Peoples.Name` and `Peoples.NameLower` are also ordered or grouped, unlike `Path`. A hash index needs its plans measured first.
- **The composites** cannot be hash indexes.
- **The most plausible failure** is a `PresentationUniqueKey` of a series without provider ids, which grows by 33 bytes of random hex per library.
- **Recommended follow-up:** measure hash for the single-column indexes, and name the composites as a PostgreSQL known gap with a kit row.

## The replay

PostgreSQL ships in the upstream series, so X12 does not ship as a migration there. In `multidb/upstream`:
- the convention and the null-order skip go into step 14;
- step 15's baseline is regenerated with the hash index;
- the paired migration, its four allowlist entries and its migration tests are dropped;
- the kit rows, `PostgresPathIndexTests` and the convention test move with step 14 or the kit step.

The fork keeps the migration, because fork databases already exist.

## Things to be ready to defend

- **Why not keep the btree and accept the gap?** PostgreSQL is Supported, and parity with SQLite is the bar. The hash index costs nothing measurable on the lookups and is a quarter of the size.
- **Why not md5, as PostgreSQL's own error hint suggests?** The hint assumes you can change the queries. Here every lookup would have to gain the md5 conjunct in shared code, or through an interceptor. Without that, every lookup by path becomes a sequential scan: 650 ms on 300,000 items.
- **Hash indexes used to be unsafe.** Before PostgreSQL 10 they were not WAL-logged. Since then they are crash-safe and replicated, and the floor is 16.
- **The empty SQLite and MySQL migrations** are the cost of the pairing rule, which the orchestrator approved over relaxing the rule. They disappear in the replay.
- **Why does Down fail with a long path?** A btree cannot hold it. Down is for development; rolling back with such data needs the item removed first.
- **What does the upgrade cost?** The migration runs at startup, in a transaction, before Jellyfin accepts clients. `DROP INDEX` takes an ACCESS EXCLUSIVE lock on `BaseItems` until the transaction ends, and the hash index is then built in the same transaction. Measured: 0.16-0.18 s for 300,001 rows with a warm cache. The reviewer's estimate is about 2-20 s per million rows cold. No client waits on it, so `CREATE INDEX CONCURRENTLY`, which cannot run in a transaction, is not worth it. The PostgreSQL how-to says so in its upgrade notes.
