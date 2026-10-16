# ADR-0012: Text-class index keys over 1700 bytes on SQL Server

- **Status:** Proposed
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

  Declared bytes are as SQL Server reports them (Msg 1945). The room is `(1700 − the other key columns) / 2`; a nonclustered index of a non-unique key also carries the clustered key, which does not count against the 1700 bytes. The table is `/tmp/sqlserver-spike/evidence/adr0012-index-table.tsv`.
- **Where the values come from.**
  - `Path` is a file path. Linux allows 4096 bytes, so an 851-unit path is possible, though rare.
  - `PresentationUniqueKey` is an id in 32 hex characters for most items. A series uses `series-<name>-<language>` plus 33 characters for each library that holds it (`Series.cs:79-119`), a season adds `-NNN`, and people, studios, genres and artists use `Type-<name>`.
  - `SortName` and `Name` are titles; `CleanName` and `CleanValue` are titles and values after cleaning; `ItemValues.Value` is already bounded at 512 (ADR-0009 L1), so `CleanValue` stays well under 848.
- **How the columns are queried** (shared code at `feature/multi-db`).
  - `Path`, `PresentationUniqueKey`, `SeriesPresentationUniqueKey`, `CleanName`, `Name` and `Peoples.Name` are only compared for equality or membership. Examples: `TranslateQuery.cs:330,335,482,1136`, `ItemPersistenceService.cs:612,698`, `NextUpService.cs:96-284`, the correlated `OrderMapper.cs:82,86` and `PeopleRepository.cs:439`.
  - `SortName` also needs order: `ORDER BY` (`QueryBuilding.cs:378`, `OrderMapper.cs`), the range filters (`QueryBuilding.cs:239,246`) and a LIKE prefix (`QueryBuilding.cs:232`).
- **What a failure costs.**
  - **A save fails loudly.** The item's whole `SaveChanges` is rejected with Msg 1946 and the exception names the index. During a scan that item is missing from the library, and the scan logs the error. No other row is affected, and nothing is truncated silently.
  - **A restore is expected to roll back.** DDL and DML are transactional on SQL Server (measured: a `CREATE TABLE` inside a rolled-back transaction is gone). The restore runs in one transaction (ADR-0003 S2), so a restore that meets such a row should roll back as a whole. Test 3 below pins this; the spike did not run it.
- **The other engines.** PostgreSQL fails above 2704 bytes (ADR-0009 G1). MySQL and MariaDB index a prefix and never fail on length (ADR-0009 L6). SQL Server is the tightest of the four.
- **Tier.** SQL Server is Experimental and fork-only (ADR-0011). ADR-0009 L2's invariant, that every row SQLite accepts can be restored on every *Supported* engine, does not bind it. ADR-0011 already lists keys over 1700 bytes as a known gap (`ADR-0009/G2`). This ADR decides whether to keep that gap or close it.

## Options considered

- **(a) Keep `nvarchar(4000)` and accept the runtime failure.** The text-class columns stay as ADR-0009 L3 has them. A row whose actual key exceeds 1700 bytes fails with Msg 1946, as measured above.
  - **Thresholds.** 850 code units for a single column; 792 to 805 units for the `BaseItems` composites at realistic values; 714 for `ProviderValue` with `ProviderId` at its bound of 128; 425 supplementary characters.
  - **Changes nothing.** Shared code, migrations on other engines and the model shape are unchanged.
- **(b) A persisted computed hash, indexed instead of the text.** For example `PathHash AS CAST(HASHBYTES('SHA2_256', [Path]) AS binary(32)) PERSISTED`, with the index on `PathHash`.
  - **Measured.** A 4000-unit value gives a 32-byte key and is accepted.
  - **CHECKSUM is unsuitable.** It is 4 bytes, collision-prone, and under BIN2 it ignores trailing spaces: `abc` and `abc␠` give the same value, `ABC` a different one.
  - **Query rewrite.** Each equality and membership predicate must compare the hash as well as the text, or the index is not used. The column is a SQL Server-only shadow property. The rewrite would live in a provider `IQueryExpressionInterceptor`, the mechanism the MySQL provider already uses (ADR-0011 F2, F7), with a provider-translated hash function over the parameter; a list goes through the same `OPENJSON` the provider already sends.
  - **Side effect.** Hash plus text equality would also make those predicates exact under trailing spaces, which narrows ADR-0006 G1.
  - **Does not serve `SortName`.** It needs order and a prefix.
  - **Cost.** A model shape that differs from SQLite (an allowance in the kit), a provider interceptor per predicate shape, and the baseline's extra columns. ADR-0009 L4 rejected the shared form of this.
- **(c) Lower bounds on SQL Server only.**
  - **Bound size.** Single-column indexes could take `nvarchar(850)`. The composites would split about 1188 bytes between up to three text columns, about 198 units each.
  - **Rejecting.** A value over the bound fails at the column (Msg 2628) instead of at the index (Msg 1946). That is earlier than (a), not later: the column bound applies to every row, while (a) fails only when the actual key overflows.
  - **Truncating.** Truncation would break equality: a truncated `Path` no longer finds its item, and a truncated key merges distinct items. With `ANSI_WARNINGS OFF` the server truncates silently (measured: 851 units became 850), which ADR-0009 L2 forbids.
  - Neither variant is better than (a).
- **(d) Drop or reshape indexes on SQL Server only**, as the MySQL provider drops filters.
  - Dropping `IX_BaseItems_Path` would leave the item lookup by path, which runs on every scan, without an index. Dropping the composites would remove the access paths they were added for (series, latest, resume, next up).
  - The single-column indexes still set an 850-unit limit per column, so (d) only moves a composite's threshold up to that of its columns.
- **(e) Included columns or filtered indexes.**
  - **INCLUDE.** Included columns do not count against the 1700 bytes; even `nvarchar(max)` can be included (measured). A composite whose trailing text column only covers a query could include it instead of keying it. For example, `(Type, TopParentId) INCLUDE (SortName)`, which drops the per-row `SortName` limit of that index from 797 to none, but loses the ordered scan for "series sorted by `SortName`". A single-column index cannot do this.
  - **Filtered.** A filtered index that leaves out long values is impossible: a filter cannot call `LEN` or `DATALENGTH` (Msg 10735, measured), and cannot reference a computed column.
- **(f) UTF-8: `varchar(n)` with `Latin1_General_100_BIN2_UTF8`.**
  - **Availability.** The binary UTF-8 collation exists on both versions (it is the only `%BIN2%UTF8` collation). It orders by code point (measured: `z < U+E000 < U+FFFD < U+1F600`), which is SQLite's order, so it would close ADR-0006 G2. Trailing spaces still compare equal, so G1 stays.
  - **Bytes per character, measured** when assigned from `nvarchar`: ASCII 1, `é` 2, CJK 3, emoji 4. The 1700-byte key therefore holds 1700 ASCII characters (twice the `nvarchar` room), 850 two-byte characters (the same), 566 CJK characters (fewer than the 850 of `nvarchar`) and 425 emoji (the same). Every threshold was measured, with Msg 1946 past it.
  - **The column counts bytes.** `varchar(4000)` rejects 1500 CJK characters (4500 bytes, Msg 2628), which `nvarchar(4000)` stores. That is the reason ADR-0006 C3 gave for not choosing UTF-8 for the key-class bounds of ADR-0009 L1, which count characters.
  - **Parameters through EF.** The probe `SqlServerUtf8ProbeTests` maps a column with `IsUnicode(false)`, `varchar(8000)` and the UTF-8 collation, as a provider convention would, and records what EF sends and what the server plans (`/tmp/sqlserver-spike/evidence/probes-c3-2022/probes.txt`):
    - **EF types the parameter from the column.** It sends `@path varchar(8000)` (`DbType.AnsiString`) for equality, for the LIKE prefix and for each value of an `IN` list.
    - **The plans seek.** Equality, prefix and `IN` all use an index seek, in a database whose default collation is UTF-8 and in one whose default is Latin1 BIN2.
    - **Where a scan comes from.** A parameter typed `nvarchar` against the `varchar` column converts the column (`CONVERT_IMPLICIT(nvarchar(4000), [Path])`) and turns the seek into an index scan (measured with `sp_executesql`). EF does not produce such a parameter for a mapped column. Only hand-written SQL or a function translation that types its argument `nvarchar` does. The default `LikeIgnoreAsciiCase` pattern is `nvarchar(4000)`, but its `lower(column)` scans anyway.
  - **Silent corruption, a data-integrity condition.** SqlClient encodes a `varchar` parameter in the code page of the database's default collation, and `CAST(… AS varchar)` uses the same code page.
    - **In a Latin1-default database.** EF's `AnsiString` parameter stores `日é😀/a` as `3F C3A9 3F3F 2F 61`: CJK and emoji become `?`, while Latin letters survive. An equality on the same value still finds the row, because the parameter is mangled the same way. Two different CJK titles therefore become the same stored value, with no error. `CAST(N'日' AS varchar(10))` is `0x3F` there.
    - **In a UTF-8-default database.** The bytes are correct (`E697A5 C3A9 F09F9880 2F 61`).
    - **No shared query casts text to `varchar`.** EF writes `CONVERT(varchar(11), …)` only for a number's `ToString()`, which is ASCII.
    - **What (f) would have to enforce.** The provider would have to refuse to start unless `DATABASEPROPERTYEX(DB_NAME(), 'Collation')` is a `*_UTF8` collation, with a message that names the setting. It would have to create its own databases with that collation. And it would need a red test that writes non-ASCII text through a `varchar` parameter in a Latin1-default database and shows the corruption.
    - **What the refusal costs.** The default collation of an existing database can only be changed with exclusive access, and not while schema-bound objects depend on it. So refusing to start puts that work on the operator, for a provider that cannot repair it.
  - **Cost.**
    - A second text type next to `nvarchar`: the key-class columns cannot move without breaking ADR-0009 L1.
    - Every text-class column, parameter and literal becomes `varchar`.
    - The UTF-8 database default above must be enforced.
    - No outcome changes for 2-byte scripts, and CJK gets worse.

## Decision (proposed)

**(a): keep `nvarchar(4000)` for the text-class columns and keep the runtime failure as the known gap `ADR-0009/G2`, made precise and pinned by tests.** Shared code does not change, so the decision is neutral to the 22d raw-SQL ban and to SQLite, PostgreSQL, MySQL and MariaDB.

- **Why (a).**
  - **The failure is safe.** It is loud and confined to one row's save. It never truncates silently, and a restore is expected to roll back as a whole (test 3).
  - **The data rarely reaches it.** The thresholds are 850 code units for a path or a name, and 714 to 805 for the composites at realistic values. The library data that would reach them is rare: a path or title over 850 characters, or a series held in more than about 20 libraries with a long name and a long episode sort name.
  - **Nothing else removes the limit.** Every other option keeps a per-row limit, except (b) for the equality-only columns:
    - (c) fails earlier;
    - (d) and (e) lose access paths and leave the single-column limit;
    - (f) moves the limit up for ASCII and down for CJK. Its price is a second text type, and a database default collation the provider must enforce at startup, because otherwise non-ASCII text is corrupted silently.
  - **(b) is for later.** It is the only real fix, and only for `Path` and the keys. It is worth its interceptor and model divergence only once the gap is seen in real libraries, which is the trigger ADR-0009 L4 already set.
- **What the provider does (step 27).**
  - It does not suppress Msg 1945. The baseline migration creates the 18 indexes as listed, and the warnings are expected.
  - It does not classify Msg 1946 as retryable.
- **The known gap.** `ADR-0009/G2` becomes a `KnownGaps` entry keyed to `SqlServer`. Its matcher accepts only a `SqlException` with number 1946, so any other failure still fails the test.
- **Revisit when** any of these holds:
  - a user report or a log shows Msg 1946 on a real library, which leads to (b) for `Path` first;
  - SQL Server is to be promoted to Supported, where ADR-0009 L2's restore invariant would bind and G2 must close: (b) for the equality-only columns, and (e) for `SortName`'s composites;
  - ADR-0006 G2's ordering becomes a promotion blocker, which leads to (f) for the text class only. It would come with the startup refusal, the creation collation and the red corruption test described under (f).

## Consequences

- No shared code, no SQLite migration, no change for the other engines.
- The SQL Server baseline (step 28) carries 18 indexes over 1700 bytes, and every migration that adds an index over a text-class column adds one more. The model test below makes each one a reviewed decision.
- Library scans on SQL Server can fail for an item whose path, name or key is over the threshold. The SQL Server how-to (step 29) states the thresholds.
- The step 27 and 28 brief, including the red-first tests, is in `pr-plan.md` under the 2026-10-16 SQL Server spike amendment (B33).

## Tests required (step 27)

1. **Model test, no server** (`SqlServerIndexKeyBudgetTests`). Every nonclustered index of the SQL Server model is within 1700 declared bytes, or is in an explicit list of over-budget indexes. Each listed entry names its text-class columns and its room in code units at the bound of its other columns (the table above). The list must equal the computed set in both directions, so the guard is non-vacuous: a new index over budget fails until it is listed, and a listed index that is no longer over budget fails until it is removed. No clustered key may exceed 900 bytes, with no list.
2. **Threshold tests, pooled backend.**
   - `Path`: 850 code units saves and 851 fails with Msg 1946, marked `KnownGap.On(SqlServer, ADR-0009/G2)`.
   - The composite `(Type, SeriesPresentationUniqueKey, PresentationUniqueKey, SortName)` behaves the same at its computed threshold.
   - Both tests pass unchanged, without the marker, on the other engines.
3. **Restore fails closed.** A backup whose item has an 851-unit `Path` fails to restore on SQL Server with Msg 1946, and the database afterwards holds exactly the rows it held before.
4. **Warning, not error, at migration.** Applying the baseline logs no error, and the 18 over-budget indexes exist.
