# ADR-0012: Text-class index keys over 1700 bytes on SQL Server

- **Status:** Accepted, by the orchestrator with the coordinator agreeing (2026-10-16), after independent review
- **Date:** 2026-10-16

## Context

- **The columns.** ADR-0009 L1 splits every string in a key or an index into key-class strings, which carry a shared `[MaxLength]`, and text-class strings, marked `IsUnboundedIndexedText()`. The text-class columns are:
  - `BaseItems.Path`, `Name`, `SortName`, `CleanName`, `SeriesName`, `PresentationUniqueKey` and `SeriesPresentationUniqueKey`;
  - `Peoples.Name` and `NameLower`;
  - `ItemValues.CleanValue`;
  - `BaseItemProviders.ProviderValue`;
  - `MediaStreamInfos.Language`.
- **How SQL Server stores them.** ADR-0009 L3 stores them as `nvarchar(4000)`. That is the longest type SQL Server can index: `nvarchar(max)` cannot be a key column at all (Msg 1919, measured).
- **The limits.**
  - A nonclustered index entry may hold 1700 bytes of key, and a clustered key 900 bytes.
  - `nvarchar` takes 2 bytes per UTF-16 code unit, so a supplementary character takes 4 bytes.
  - SQL Server warns when it creates an index whose declared key can exceed the limit (Msg 1945). It then fails each insert or update whose actual key exceeds it (Msg 1946).
- **Measured** on SQL Server 2022 CU27 (16.0.4295.3) and 2025 CU9 + GDR (17.0.5015.4), with identical results. The DDL comes from the conventions-only stub of the SQL Server spike. Evidence is under `/tmp/sqlserver-spike/evidence/` (`adr0012-*`), and the full checklist is in [the spike explainer](explainers/sqlserver-spike.md).
  - **Schema.** 31 tables with their primary keys, and 65 indexes, 10 of them unique. 18 nonclustered indexes are over 1700 bytes declared: every index that contains a text-class column (table below). No clustered key exceeds 900 bytes; the largest is `PK_PeopleBaseItemMap` at 832. So the ADR-0009 L1 bounds hold, and this ADR is about the nonclustered text-class indexes only.
  - **Single column.** An index of the same shape as `IX_BaseItems_Path` (`nvarchar(4000)` under BIN2, in the probe table `a_single`) takes 850 code units (1700 bytes) and fails at 851: *"Msg 1946 … The index entry of length 1702 bytes for the index 'IX_a_single_Path' exceeds the maximum length of 1700 bytes for nonclustered indexes."* 425 emoji are accepted and 426 fail at 1704 bytes. An `UPDATE` past the limit fails the same way, and a value over 4000 code units fails the column itself (Msg 2628).
  - **Composite.** The sum of the actual values counts. In `(Type, SeriesPresentationUniqueKey, PresentationUniqueKey, SortName)`, an episode row with a 43-character type, a 116-character series key (`series-<40-character name>-en-` and two library ids) and a 32-character key stores a 659-unit sort name, and fails at 660 (1702 bytes).
  - **Clustered key.** 450 code units are accepted and 451 fail at 902 bytes.

| Index (all on `BaseItems` unless named) | Text-class key columns | Declared bytes | Room for the text, code units, with the other columns at their bound | With a 45-character `Type` |
|---|---|---|---|---|
| `IX_BaseItems_Path` | `Path` | 8000 | 850 | 850 |
| `IX_BaseItems_Name` | `Name` | 8000 | 850 | 850 |
| `IX_BaseItems_PresentationUniqueKey` | `PresentationUniqueKey` | 8000 | 850 | 850 |
| `IX_BaseItems_SeriesName` | `SeriesName` | 8000 | 850 | 850 |
| `IX_Peoples_Name`, `IX_Peoples_NameLower` | `Name`, `NameLower` | 8000 each | 850 | 850 |
| `IX_ItemValues_Type_CleanValue` | `CleanValue` | 8004 | 848 | 848 |
| `IX_MediaStreamInfos_StreamType_ItemId_Language_IsExternal` | `Language` | 8021 | 839 | 839 |
| `IX_BaseItems_IsFolder_TopParentId_IsVirtualItem_PresentationUniqueKey_DateCreated` | `PresentationUniqueKey` | 8025 | 837 | 837 |
| `IX_BaseItems_MediaType_TopParentId_IsVirtualItem_PresentationUniqueKey` | `PresentationUniqueKey` | 8145 | 777 | 777 |
| `IX_BaseItemProviders_ProviderId_ItemId_ProviderValue` | `ProviderValue` | 8272 | 714 | 714 |
| `IX_BaseItems_Type_CleanName` | `CleanName` | 8512 | 594 | 805 |
| `IX_BaseItems_Type_SeriesPresentationUniqueKey_IsFolder_IsVirtualItem` | `SeriesPresentationUniqueKey` | 8513 | 593 | 804 |
| `IX_BaseItems_Type_SeriesPresentationUniqueKey_ParentIndexNumber_IndexNumber` | `SeriesPresentationUniqueKey` | 8520 | 590 | 801 |
| `IX_BaseItems_Type_TopParentId_SortName` | `SortName` | 8528 | 586 | 797 |
| `IX_BaseItems_Type_TopParentId_PresentationUniqueKey` | `PresentationUniqueKey` | 8528 | 586 | 797 |
| `IX_BaseItems_Type_TopParentId_IsVirtualItem_PresentationUniqueKey_DateCreated` | `PresentationUniqueKey` | 8537 | 581 | 792 |
| `IX_BaseItems_Type_SeriesPresentationUniqueKey_PresentationUniqueKey_SortName` | `SeriesPresentationUniqueKey`, `PresentationUniqueKey`, `SortName` (shared) | 24512 | 594 | 805 |

  Declared bytes are the ones SQL Server reports in its Msg 1945 warnings (`adr0012-create-warnings-*.txt`). The computed table `/tmp/sqlserver-spike/evidence/adr0012-index-table.tsv` counts a `bit` as one byte and gives 8026 and 8514 for the two indexes with two `bit` columns, where the server reports 8025 and 8513. The room is `(1700 − the other key columns) / 2`. A nonclustered index of a non-unique key also carries the clustered key, which does not count against the 1700 bytes.
- **Where the values come from.**
  - **`Path`** is a file path. Linux allows 4096 bytes, so an 851-unit path is possible: a deep NAS path with long folder and file names is the realistic first case.
  - **`PresentationUniqueKey`** is an id in 32 hex characters for most items. A series with at least two user-data keys (a provider id) uses the first of them. Only a series without one uses `series-<lowercased name>` (`Series.cs:87-89`), and that is the long form. Either form gets `-<language>` and 33 characters for each library that holds it (`Series.cs:100-119`). A season adds `-NNN`; people, studios, genres and artists use `Type-<name>`.
  - **`SortName` and `Name`** are titles. `CleanName` and `CleanValue` are titles and values after cleaning. `ItemValues.Value` is already bounded at 512 (ADR-0009 L1), so `CleanValue` stays well under 848.
- **How the columns are queried** (shared code at `feature/multi-db`).
  - `Path`, `PresentationUniqueKey`, `SeriesPresentationUniqueKey`, `CleanName`, `Name` and `Peoples.Name` are only compared for equality or membership. Examples: `TranslateQuery.cs:330,335,482,1136`, `ItemPersistenceService.cs:612,698`, `NextUpService.cs:96-284`, the correlated `OrderMapper.cs:82,86` and `PeopleRepository.cs:439`.
  - `SortName` also needs order: `ORDER BY` (`QueryBuilding.cs:378`, `OrderMapper.cs`), the range filters (`QueryBuilding.cs:239,246`) and a LIKE prefix (`QueryBuilding.cs:232`).
- **What a failure costs: a whole folder, not one row.**
  - **The save is a batch.** A folder's scan collects every new child and calls `LibraryManager.CreateItems` once (`Folder.cs:467-610`). `UpdateItemsAsync` batches the same way (`LibraryManager.cs:2716`). Both go to `ItemPersistenceService.SaveItems`, which saves the whole batch in one transaction (`SaveItemTuples`, `ItemPersistenceService.cs:369-371`, committed at `:825-826`). One over-long row rolls back the save of every item in it.
  - **The scan skips the rest of that folder.** Nothing in `Folder.ValidateChildrenInternal2` catches the exception. The folder's validation stops at `CreateItems` (`Folder.cs:610`), before its subfolders are validated (`:770`) and its children's metadata is refreshed (`:809`). `LimitedConcurrencyLibraryScheduler.ProcessItem` logs *"Error while performing a library operation"* and goes on with the sibling folders (`LimitedConcurrencyLibraryScheduler.cs:254-274`).
  - **It never heals.** The next scan does the same, so the other new items of that folder, and the new content of its existing subfolders, never appear while the long item is there. If that folder is a library root with files at its top level, the whole library stops gaining items.
  - **Measured, through the real `SaveItems`** (`evidence/batch-save/`, a scratch test that is not committed). 50 new movies, one of them with an over-long `Path`, in one call. The logs record no server version: SQL Server 2022 CU27, PostgreSQL 16.15 and MySQL 8.4.11 are inferred from the test backends' default images at `c0b4bcc44b`.
    - **SQL Server 2022:** Msg 1946 (*"index entry of length 1818 bytes for the index 'IX_BaseItems_Path'"*), 0 of 50 saved.
    - **PostgreSQL 16.15**, a Supported engine (3000 random characters): `54000` *"index row size 3024 exceeds btree version 4 maximum 2704 for index "IX_BaseItems_Path""*, 0 of 50 saved.
    - **SQLite**, with a trigger that raises an error for a path over 850 characters: 0 of 50 saved.
    - **MySQL 8.4** (prefix index): 50 of 50 saved.

    So the batch effect is not particular to SQL Server. It applies to any error of one row on any engine, and today it applies to ADR-0009 G1 on PostgreSQL.
  - **A restore is expected to roll back as a whole.** DDL and DML are transactional on SQL Server (measured: a `CREATE TABLE` inside a rolled-back transaction is gone). The restore runs in one transaction (ADR-0003 S2), so a restore that meets such a row should roll back as a whole. Step 27 pins this with a red test; the spike did not run it.
  - **No silent truncation.** It holds with `ANSI_WARNINGS ON`, which every measured session had (probe `Sessions_*`). With `ANSI_WARNINGS OFF`, the server cuts a value at the column bound silently (measured: 851 units became 850).
- **The other engines.** PostgreSQL fails above 2704 bytes (ADR-0009 G1), with the same batch effect. MySQL and MariaDB index a prefix and never fail on length (ADR-0009 L6). SQL Server is the tightest of the four.
- **Tier.** SQL Server is Experimental and fork-only (ADR-0011). ADR-0009 L2's invariant, that every row SQLite accepts can be restored on every *Supported* engine, does not bind it. ADR-0011 already lists keys over 1700 bytes as a known gap (`ADR-0009/G2`). This ADR decides how SQL Server stores and indexes the text-class columns, and what limits the damage when a key is too long.

## Options considered

Two kinds of option are weighed. The storage and index options decide when a key is too long. The blast-radius options decide what an over-long key costs.

- **(a) Keep `nvarchar(4000)` and accept the runtime failure.** The text-class columns stay as ADR-0009 L3 has them, and a row whose actual key exceeds 1700 bytes fails with Msg 1946.
  - **Thresholds.** 850 code units for a single column; 792 to 805 for the `BaseItems` composites at realistic values; 714 for `ProviderValue` with `ProviderId` at its bound of 128; 425 supplementary characters.
  - **Cost.** Alone, it carries the whole batch effect above.
- **(i) A pre-save check in the SQL Server provider.** A `SaveChangesInterceptor` computes, for every added or modified entry, the key bytes of each index over a text-class column. It fails before any SQL is sent, with an exception that names the item and the index.
  - **Prototype measured.** The interceptor in the scratch test gives *"Item 2072a972-… cannot be saved on SQL Server: its key for index IX_BaseItems_Path takes 1818 bytes, over the limit of 1700"*. That is the same byte count the server reports, and no `INSERT` into `BaseItems` is sent.
  - **Same blast radius.** It still fails the whole batch: 0 of 50 saved. It only makes the failure attributable to a file.
  - **Cost.** It repeats the server's arithmetic in the provider: 2 bytes per code unit, plus each fixed type's width. It is provider-only and neutral to shared code.
- **(S) Save item by item when a batch fails, in shared code.** When `SaveItems` fails on a batch of more than one item with an error of a row or of its data, it saves the items again one at a time, each in its own transaction, in the list's order. It then logs and skips only the items that fail, naming the item, its path and the cause.
  - **The trigger.** A `DbUpdateException` whose innermost `DbException` has `IsTransient == false` and is not a retryable write conflict for the provider (`IsRetryableWriteConflict`). `DbException.IsTransient` is ADO.NET's own flag, which SqlClient, Npgsql, MySqlConnector and Microsoft.Data.Sqlite set.
  - **What never triggers it:** connection failures, command time-outs (a `TimeoutException`, or SqlClient's number −2), transport and I/O errors, cancellation, and any exception that is not a `DbUpdateException`. Those are rethrown as today.
  - **Stop early.** If the first K per-item saves all fail with the same exception type and provider error number, the fallback stops and rethrows the last error. The proposed default is K = 3; the step's design fixes it.
  - **Measured** (emulated from the test by calling `SaveItems` once per item after the failed batch): 49 of 50 saved, and only the over-long item fails, with its error, on SQL Server, PostgreSQL and SQLite. The fallback took about 1.6 s on SQL Server, 0.2 s on PostgreSQL and 0.1 s on SQLite for 50 items. It runs only after a batch failed, and costs nothing on the normal path.
  - **What it fixes.** With the save succeeding for the rest, `CreateItems` returns, and the scan goes on into the folder's subfolders and its metadata refresh. The cost of an over-long key becomes one item, or one folder's subtree, missing and logged on every scan. This holds on every engine and for any error of one row, not only for key length.
  - **A subtree can still be lost.** A folder whose own path is over the limit is skipped. Its children's paths are longer still, or reference the missing folder, so its next scan fails alike, the stop-early rule rethrows, and that folder's subtree is lost. Its sibling folders are not affected.
  - **Masking real errors.** A skipped item is logged at Error level on every scan, with its identity and the exception, so it is not lost silently. Deadlocks and unique-key races are retried by `WriteConflictRetry` (step 07) before the fallback sees them. Connection and time-out errors never reach it (the trigger above), and the stop-early rule bounds what a systemic error that slips through can cost.
  - **Consistency.**
    - Each item's rows (the item, its ancestors, values, people, providers, streams) stay atomic, because each item is saved in its own transaction.
    - The folder's set of new children becomes partial, which is the point.
    - `CreateItems` appends resolved local alternate versions after their primaries (`LibraryManager.cs:2446-2491`). The fallback keeps that order and skips an alternate whose primary failed, so no alternate points at a missing primary.
    - New children reference their folder, which already exists. Whether extras can share a batch with their owner (`OwnerId` is a foreign key) is not checked here; the step must check it and skip dependents of a failed item the same way.
  - **Shared-code impact.** One method in `ItemPersistenceService`, with no engine branch and no raw SQL, so it is neutral under the 22d ban. It changes upstream behaviour on every engine, though, so it is its own upstream-shaped step with failing-first tests on all engines, not part of the SQL Server work.
- **(b) A persisted computed hash, indexed instead of the text.** `PathHash AS CAST(HASHBYTES('SHA2_256', [Path]) AS binary(32)) PERSISTED`, with the index on `PathHash` instead of `Path`.
  - **Measured** (`adr0012-hash-plans-2022.txt`):
    - a 4000-unit value gives a 32-byte key; with only the hash indexed, `Path` can even be `nvarchar(max)`, and a 5000-character path was stored;
    - `WHERE Path = @p` alone becomes a clustered index scan;
    - with `PathHash = HASHBYTES('SHA2_256', @p) AND Path = @p` it is an index seek and a key lookup;
    - a predicate on the hash expression itself is matched to the computed column's index automatically.
  - **CHECKSUM is unsuitable.** It is 4 bytes, collision-prone, and under BIN2 it ignores trailing spaces.
  - **Query rewrite.** Each equality and membership predicate on the column must gain the hash conjunct. A provider `IQueryExpressionInterceptor`, the mechanism the MySQL provider already uses (ADR-0011 F2, F7), can add it for `Path ==` and `paths.Contains(Path)`. A list goes through the same `OPENJSON` parameter. Hash plus text equality also makes those predicates exact under trailing spaces, which narrows ADR-0006 G1.
  - **Scope.** It serves the equality-only columns, `Path` first. It does not serve `SortName`, which needs order and a prefix.
  - **Cost.**
    - A model shape that differs from SQLite (an allowance in the kit).
    - The provider interceptor.
    - A guard test that every `Path` predicate the shared code sends seeks on SQL Server, because a new predicate shape would scan.
    - ADR-0009 L4 rejected the shared form of this.
- **(c) Lower bounds on SQL Server only.**
  - **Bound size.** Single-column indexes could take `nvarchar(850)`. The composites would split about 1188 bytes between up to three text columns, about 198 units each.
  - **Rejecting.** A value over the bound fails at the column (Msg 2628), earlier than (a), and with the same batch effect.
  - **Truncating.** Truncation would break equality, and with `ANSI_WARNINGS OFF` the server truncates silently, which ADR-0009 L2 forbids.
  - Neither variant is better than (a).
- **(d) Drop or reshape indexes on SQL Server only**, as the MySQL provider drops filters.
  - Dropping `IX_BaseItems_Path` would leave the item lookup by path, which runs on every scan, without an index. Dropping the composites would remove the access paths they were added for.
  - The single-column indexes still set an 850-unit limit per column.
- **(e) Included columns or filtered indexes.**
  - **INCLUDE.** Included columns do not count against the 1700 bytes; even `nvarchar(max)` can be included (measured). A composite whose trailing text column only covers a query could include it instead of keying it, at the cost of its ordered scan. A single-column index cannot do this.
  - **Filtered.** A filter cannot call `LEN` or `DATALENGTH` (Msg 10735, measured in `adr0012-keys-*.txt`), and cannot reference a computed column.
- **(f) UTF-8: `varchar(n)` with `Latin1_General_100_BIN2_UTF8`.** The measurements are in `adr0012-utf8.txt` and `adr0012-utf8-plans.txt`, and the EF probe output in `probes-c3-2022/probes.txt`. Section (f) of `adr0012-keys-*.txt` was an early probe that converted through the database's Latin1 code page. Its CJK and emoji rows are void and `adr0012-utf8.txt` supersedes them; its ASCII and `é` rows stand.
  - **Collation and order.** The binary UTF-8 collation exists on both versions (the only `%BIN2%UTF8` one). It orders by code point (`z < U+E000 < U+FFFD < U+1F600`), so it would close ADR-0006 G2. Trailing spaces still compare equal.
  - **Bytes per character** when assigned from `nvarchar` (`adr0012-utf8.txt`): ASCII 1, `é` 2, CJK 3, emoji 4. The 1700-byte key therefore holds 1700 ASCII characters (twice `nvarchar`'s room; 1701 fails, from `adr0012-keys-*.txt`), 850 `é` (851 fails), 566 CJK characters (567 fails) and 425 emoji (426 fails). Each failure is Msg 1946.
  - **The column counts bytes.** `varchar(4000)` rejects 1500 CJK characters (4500 bytes, Msg 2628), which `nvarchar(4000)` stores. That is ADR-0006 C3's reason against UTF-8 for the key-class bounds of ADR-0009 L1.
  - **Parameters through EF.** For a column mapped with `IsUnicode(false)`, EF sends `varchar` parameters, and equality, LIKE prefix and `IN` lists all seek. An `nvarchar` parameter converts the column and scans, but only hand-written SQL or a function translation that types its argument `nvarchar` produces one.
  - **Silent corruption.** In a database whose default collation is not UTF-8, EF's `varchar` parameter stores `日é😀/a` as `3F C3A9 3F3F 2F 61`. An equality on the same value still finds the row, because the parameter is mangled the same way. From that it follows, though it was not measured directly, that two different CJK titles would be stored as the same value. The bytes are correct in a database whose default is UTF-8.
  - **What (f) would have to enforce.** Refusing to start unless the database default is `*_UTF8`; creating its own databases with it; and a red test that shows the corruption in a Latin1-default database. The operator could only change an existing database's default with exclusive access.
  - **Same blast radius as (a).** It moves the limit up for ASCII and down for CJK.

### What each option means for `Path`

| Option | Over-long `Path` | `Path` equality and `IN` | Shared query changes | Shared-code impact (22d) |
|---|---|---|---|---|
| (a) | fails at 851 units, whole batch | seek on `IX_BaseItems_Path` | none | none |
| (i) | fails before SQL at 851 units, naming item and index; whole batch | seek | none | none, provider interceptor |
| (S) | the item (or an over-long folder's subtree) is skipped and logged; the rest of the batch saves | seek | none | one method in `ItemPersistenceService`, engine-neutral, no raw SQL; its own upstream-shaped step |
| (b) for `Path` | no limit (`nvarchar(max)` possible) | seek on the hash index once the provider adds the hash conjunct; scan without it | none | none, provider interceptor and a SQL Server-only computed column |
| (c) | fails at the column, whole batch | seek | none | none |
| (d) | without the index: no limit | scan on every scan of the library | none | none |
| (e) | not applicable to the single-column `Path` index | seek | none | none |
| (f) | fails at 1701 ASCII bytes or 567 CJK characters, whole batch | seek, with `varchar` parameters | none | none, but a startup refusal |

## Decision

1. **Storage stays (a).** The text-class columns keep `nvarchar(4000)` on SQL Server, and keys over 1700 bytes stay the known gap `ADR-0009/G2`. It becomes a `KnownGaps` entry keyed to `SqlServer`, whose matcher accepts only `SqlException` number 1946. No other storage option removes the limit for every column. (b) removes it for `Path` alone. (f) trades ASCII room for CJK room behind a startup refusal.
2. **The blast radius is limited by (S), as the independent shared step X11, `fix/save-items-per-item-fallback`, cut from `upstream/master` and merged into `feature/multi-db` before step 28.**
   - **Why.** The batch effect is the real cost, and it is not particular to SQL Server. It applies today to PostgreSQL, a Supported engine (ADR-0009 G1, measured), and to any error of one row on any engine.
   - **What changes.** (S) turns "a folder stops gaining items" into "one item, or one folder's subtree, is missing and logged". It costs nothing on the normal path, and it needs no change to any query.
   - **Where it goes.** It is an engine-neutral change to upstream behaviour, so it does not go into the SQL Server steps. It is the independent unit X11 in `pr-plan.md` (B33), cut from `upstream/master`, with failing-first tests on every engine that use a deliberately failing row. It is a candidate for the upstream series.
3. **Only if X11 is rejected, the SQL Server provider takes (b) for `Path` in step 27, together with (i).**
   - (b) removes the realistic first case: an over-long path, now with no limit and still a seek.
   - (i) makes any other text-class overflow name its item and index.
   - SQL Server is not registered (step 28) with the folder-wide failure mode and no mitigation.
   - Under this fallback, the folder-wide failure mode stays for the other 11 text-class columns. Only `Path` loses its limit.
4. **(i) is not adopted alongside (S).** With (S), the per-item save already attributes the failure to the item, and the server's message names the index. (i) would duplicate the server's arithmetic for no change in outcome.
5. **What the provider does in step 27 either way.** It does not suppress Msg 1945, because the 18 indexes are created as listed and the warnings are expected. It does not classify Msg 1946 as retryable.
6. **Revisit triggers.**
   - **A Msg 1946 on a real library's `Path`** leads to (b) for `Path`. With (S) it shows as a skipped item in the log, and without (S) as a folder that stops growing.
   - **SQL Server is to be promoted to Supported.** ADR-0009 L2's restore invariant would then bind, so G2 must close: (b) for the equality-only columns, and (e) for `SortName`'s composites.
   - **ADR-0006 G2's ordering becomes a promotion blocker.** That leads to (f) for the text class only, with the enforcement described under (f).
7. **The SQL Server how-to (step 29)** says plainly: an item whose path is longer than about 850 characters, or 425 characters outside the Basic Multilingual Plane such as emoji, is not saved on SQL Server. It then says what happens to the rest of its folder. Without (S), none of the folder's new items are saved and its subfolders are not scanned. With (S), only that item is missing, or, for an over-long folder, that folder's subtree, and the log names it.

8. **PostgreSQL keeps a known gap that X11 does not close.** On PostgreSQL, a Supported engine, an item whose `Path` index key exceeds 2704 bytes never saves, while SQLite saves it (ADR-0009 G1; measured: `54000` at 3024 bytes).
   - That is a parity gap on a Supported engine. X11 only stops it from costing the rest of the folder.
   - At `feature/multi-db`, no `KnownGaps` entry and no test pin it.
   - The follow-up is X12 in `pr-plan.md` (B34). The step that closes it adds a pinning test first: a `Path` over 2704 bytes saves on every engine, red on PostgreSQL.

## Consequences

- **Storage.** No shared code, no SQLite migration and no change for the other engines.
- **Indexes.** The SQL Server baseline (step 28) carries 18 indexes over 1700 bytes, and every migration that adds an index over a text-class column adds one more. The budget test below makes each one a reviewed decision.
- **(S) is a shared change of its own.** It changes what a failed batch costs on SQLite, PostgreSQL, MySQL and MariaDB too, and it needs its own review.
- **The brief.** The step 27 and 28 brief, including the red-first tests, is in `pr-plan.md` under the 2026-10-16 SQL Server spike amendment (B33).

## Tests required (step 27)

1. **Model test, no server** (`SqlServerIndexKeyBudgetTests`). Every nonclustered index of the SQL Server model is within 1700 declared bytes, or is in an explicit list of over-budget indexes. Each listed entry names its text-class columns and its room in code units at the bound of its other columns (the table above). The list must equal the computed set in both directions, so the guard is non-vacuous: a new index over budget fails until it is listed, and a listed index that is no longer over budget fails until it is removed. No clustered key may exceed 900 bytes, with no list.
2. **Threshold tests, pooled backend.**
   - `Path`: 850 code units saves and 851 fails with Msg 1946, marked `KnownGap.On(SqlServer, ADR-0009/G2)`.
   - The composite `(Type, SeriesPresentationUniqueKey, PresentationUniqueKey, SortName)` behaves the same at its computed threshold.
   - Both tests pass unchanged, without the marker, on the other engines.
3. **Batch save.** `SaveItems` with N new children of one folder, one of them with an 851-unit `Path`. The test pins the actual behaviour per engine:
   - **Without (S):** no item is saved, and the exception is Msg 1946 on SQL Server and `54000` on PostgreSQL for a path over its limit.
   - **With (S):** N − 1 items are saved, and the one is logged.

   A scan-level variant asserts that the folder's subfolders are, or are not, validated.
4. **Restore fails closed (red first).** A backup whose item has an 851-unit `Path` fails to restore on SQL Server with Msg 1946, and the database afterwards holds exactly the rows it held before.
5. **Warning, not error, at migration.** Applying the baseline logs no error, and the 18 over-budget indexes exist.
6. **If (b) is taken.** Every `Path` predicate the shared code sends seeks on SQL Server (captured SQL and plan), and a 5000-character `Path` is saved.
