# Explainer: the SQL Server spike

These are fork-internal study notes, not upstream PR text. They record the SQL Server spike that ADR-0001 E3 asks for before step 27: the whole suite against a conventions-only provider stub, and a hazard checklist built from what failed. The spike also measured the text-class index question, which [ADR-0012](../0012-sqlserver-text-index-keys.md) settles (Proposed). The brief for steps 27 and 28 is in `pr-plan.md`, B33.

The spike code is on `spike/sqlserver` and is not for merge. Its base is `8845731f10`, the docs-only tip that recorded the end of 27a; the spike was first cut from `a142db1590` and rebased onto it. Evidence is under `/tmp/sqlserver-spike/`.

| Commit | What it holds |
|---|---|
| `310c4522af` | **The conventions-only stub.** <ul><li>The packages: `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12, and `Testcontainers.MsSql` 4.16.0 pinned centrally.</li><li>The conventions: `binary(16)` Guids in canonical byte order, `Latin1_General_100_BIN2` per text column, `nvarchar(4000)` for the text class, the OR-shaped filter dropped, `NoAction` on `BaseItems.ParentId` and `AncestorIds.ParentItemId`, and `IdentifierLengthConvention(128)`.</li><li>A purge that deletes with the constraints off and reseeds only the identities that were used.</li><li>A throwaway generated baseline.</li><li>On the test side: the pool backend, `EngineTraits` and kit rows, `sqlserver.runsettings`, and SQL Server in the pool self-tests and the integration factory tests.</li><li>Registration, only so that the integration tests can start the server.</li></ul> |
| `a9f63bf1e1` | **Probes** that write what they find to the test output: query shapes and EF options, session settings, the pooled-connection isolation level, error numbers, service-provider sharing, and the UTF-8 option through EF. |
| `c0b4bcc44b` | **The hooks the first run asked for, to see what remains behind them:** <ul><li>`IDENTITY_INSERT`;</li><li>the retry classifier;</li><li>`LikeIgnoreAsciiCase` through `TRANSLATE`;</li><li>`nvarchar(max)` for unindexed bounded text;</li><li>`ReadCommittedConnection`;</li><li>a `datetime2` constant for `DATEADD`;</li><li>four test-side fixes, and a ledger entry for the sort-name range filters.</li></ul> |

**Gates.** Each commit was built in a new detached worktree, with `Release --no-incremental`, `Debug --no-incremental` and `dotnet format --verify-no-changes --verbosity minimal` (`/tmp/sqlserver-spike/gates/c{1,2,3}-*/results.tsv`). Every commit gives Release 0 warnings and 0 errors, Debug 0 errors with 211 warnings, and a clean format.
- **The Debug warning set did not grow**, not even inside the spike provider: it equals the base's set (`base-debug/warnings.norm`, built at `8845731f10`).
- **Two first forms were amended.**
  - The first form of the stub commit (`ceabbfb1d9`) had CA2213 in `SqlServerTestBackend`. That analyzer runs only in Debug, so Release did not catch it.
  - The first form of the probes (`bee59a82a9`) failed in Release too: CS1061, plus analyzer errors (`gates/c2-bee59a82a9-did-not-compile/`).

## The runs

| Run | Commit | Server | Implementations | Server.Tests | Integration | Evidence |
|---|---|---|---|---|---|---|
| 1, stub | `ceabbfb1d9` (= `310c4522af` without the CA2213 fix; the run has `start-identity.txt` but no `end-identity.txt`, because the worktree had uncommitted edits when it ended) | 2022 CU27, 16.0.4295.3 | 2464 passed, 86 failed, 144 skipped | 126 / 9 / 30 | 140 / 0 / 8 | `evidence/run1-2022/` |
| 2, stub plus the 7 probe tests | `a9f63bf1e1` (also no `end-identity.txt`, for the same reason) | 2025 CU9 + GDR, 17.0.5015.4 | 2472 / 85 / 144 | 126 / 9 / 30 | 140 / 0 / 8 | `evidence/run2-2025/` |
| 3, stub and hooks | `c0b4bcc44b` | 2022 CU27 | 2544 / 5 / 156 | 126 / 0 / 39 | 140 / 0 / 8 | `evidence/run3-2022/` |
| 3, stub and hooks | `c0b4bcc44b` | 2025 CU9 + GDR | 2539 / 10 / 156 | 126 / 0 / 39 | 140 / 0 / 8 | `evidence/run3-2025/` |
| The four existing engines | `c0b4bcc44b` | SQLite 3.53.3, PostgreSQL 16.15, MySQL 8.4.11, MariaDB 11.4.13 | 12 legs, 12,123 rows: 11,482 passed, 641 skipped, 0 failed; every sentinel passed | | | `evidence/four-engines/` |

- **Sentinels.** Every SQL Server leg passed its project's sentinel, and the backend reported its version. The integration sentinel ran the server on the SQL Server provider.
- **The conformance kit on the stub** (run 1): 55 of 64 rows. Like failed 4, WriteConflict 2, Restore 2 and Model 1. Every other row passed:
  - IdList, with 100,000 ids through both `WhereOneOrMany` and a plain `Contains`;
  - FolderDelete (M5);
  - Purge, with the identity reset inside and outside a transaction;
  - DateTimeLimits and CompositeKey.
- **2022 against 2025, on the stub.**
  - **Run 2 is not the same commit as run 1.** It is run 1's stub plus the 7 probe tests. Its failures are 86 − 4 + 2 + 1 = 85:
    - the four `ModelShapeTests` rows that failed on 2022 pass;
    - two `MySqlCollationModelTests` rows fail instead, all six with EF's process-wide `ManyServiceProvidersCreatedWarning`;
    - one probe fails: `Sessions_OfTheProvider_RunAtReadCommittedWithTheExpectedSettings`.
  - **That probe failed identically on 2022** (`probes-2022`) and 2025. Its own `CONCAT` over `sys.databases` columns met a collation conflict (`SQL_Latin1_General_CP1_CI_AS` against `Latin1_General_CI_AS_KS_WS`). It is a probe artefact, fixed in `c0b4bcc44b`. A startup check in the provider must avoid the same trap (B33).
  - **The warning is test-side and order-dependent.** Both warning classes pass when run alone (`evidence/many-service-providers-isolated.txt`), and the provider shares one service provider across contexts and pooled databases (probe).
  - **No test changed outcome for another reason**, so the conclusion stands: no behavioural difference between 2022 and 2025.
- **What the hooks left on 2022** (run 1 against run 3, test by test).
  - **73 of the 86 failures pass.**
  - **12 are skipped:** 10 `EnforceKeyLengthsTests` seeds of over-bound key-class rows, which the tests now skip on SQL Server as on MySQL, and the 2 sort-name range rows, which are now in the ledger.
  - **One still fails:** `Model_OfTheSelectedProvider_HasTheShapeOfTheSqliteModel`, which needs the allowances of row 26.
  - **The 9 Server.Tests failures are skipped** for the same over-bound reason.
  - **No new failure is a defect.** The other four are the order-dependent `ManyServiceProvidersCreatedWarning`, this time in `ConnectionStringOverrideTests` (× 2) and in the UTF-8 probe (× 2), whose own contexts each build a service provider through `LogTo`.
- **Open: the service-provider warning.** Why a SQL Server run crosses EF's count of 20 internal service providers, when the other engines' runs do not, is not explained. The provider's own options share one service provider across contexts and pooled databases. Step 27 should log EF's service-provider creation in one SQL Server run and fix the creator, or let the tests that build models on purpose ignore the warning.
- **Probes.** On both servers: `evidence/probes-2022`, `probes-2025` (stub), and `probes-c3-2022`, `probes-c3-2025` (hooks). SQL-level probes in the spike's own labelled containers are `evidence/semantics-*.txt`, `adr0012-*`, `model-creation-errors.txt`, `dateadd-literal-2022.txt` and `quoted-identifier.txt`.

## The hazard checklist

Status is one of: **works**; **convention** (needs a provider convention); **hook** (needs a provider member or EF extension); **shared** (a shared-code issue); **gap** (a known gap); **test** (test side only).

| # | Hazard | Status | Evidence | Resolution for step 27 |
|---|---|---|---|---|
| 1 | Guid order and `MIN` | works, with the converter | `MIN([b].[Id])` over `binary(16)` (probe "Min Guid per group"); grouping tests pass; `binary(16)` sorts `0x0FFF…FF` before `0x1000…00`. `MIN(uniqueidentifier)` exists on 2022 CU27 and 2025, but orders `10000000-…` before `0FFFFFFF-…`, the last 6 bytes first (`semantics-*.txt`). | Keep the big-endian `binary(16)` converter (ADR-0007 G2, P2). ADR-0007 G1's "SQL Server has no `MIN` over `uniqueidentifier`" is wrong for these versions; the order problem it guards against is real. Record the correction. |
| 2 | Guid lists | works | `WhereOneOrMany` sends one `OPENJSON` parameter with the Guids as base64 into `binary(16)`; kit IdList passes. | None. |
| 3 | Cascade cycles | convention | Msg 1785 for `FK_BaseItems_BaseItems_ParentId` (self cascade) and for `FK_AncestorIds_BaseItems_ParentItemId` (second path), on both servers (`model-creation-errors.txt`). With `NoAction` the schema builds, and FolderDelete passes 2/2. | `NoAction` on the two keys (M5). The kit needs a narrow allowance (row 26). |
| 4 | OR filter | convention | Msg 156 *"Incorrect syntax near the keyword 'OR'"* on both servers. | Drop exactly that filter; the three single-predicate filters stay (M3). |
| 5 | `QUOTED_IDENTIFIER` | works | SqlClient sessions have `quoted_identifier=1` (probe). With it off at parse time, a double-quoted filter fails with Msg 10735 (`quoted-identifier.txt`). The line in `semantics-*.txt` that says such a filter was created is an artefact: the filter was run through `EXEC`, which parsed it with the setting on. | A test that asserts it, as M3 requires. |
| 6 | Unique index with several NULLs | works | EF adds `[UserId] IS NOT NULL` to `IX_ImageInfos_UserId` itself (generated baseline; M6). | The M6 model test. |
| 7 | Keys of 900 and 1700 bytes | gap (text class); works (key class) | 18 nonclustered indexes are over 1700 bytes declared (Msg 1945); an actual key over 1700 fails with Msg 1946 at 851 code units. The largest clustered key is 832 bytes (`PK_PeopleBaseItemMap`). | [ADR-0012](../0012-sqlserver-text-index-keys.md): keep `nvarchar(4000)`, ledger entry `ADR-0009/G2`, budget model test. |
| 8 | `nvarchar(max)` in an index | convention | Msg 1919 for `Path` as `nvarchar(max)` (both servers). | `nvarchar(4000)` for the text class (ADR-0009 L3). |
| 9 | Bounded text that is not indexed | convention | Msg 2628 for `ActivityLogs.Overview` (512) and `ImageInfos.Path` (512): `TextColumnLengthTests` × 2 and `EnforceKeyLengthsTests.SaveChanges_ColumnOutsideAnyIndex_IsNotLimited` (the `Overview` case), which passes in run 3. | `nvarchar(max)` for every bounded string outside keys and indexes (ADR-0009 L3), as the MySQL `UnindexedTextConvention` does. Done in `c0b4bcc44b`; 19 columns change. |
| 10 | Key-class bounds | works; test | Msg 2628 when a test seeds a legacy row over its bound: `EnforceKeyLengthsTests` × 10, `NormalizeOverlongKeysTests` × 9. These tests already skip MySQL and MariaDB for the same reason. | An `EngineTraits` capability (row 26) instead of the provider lists in those tests. |
| 11 | `LIKE` and `[` | works | `LikePatterns` escapes `[`, and EF escapes it in `StartsWith` (`LIKE N'a\[b%' ESCAPE N'\'`). The kit's 7 bracket rows pass. Unescaped, `[b]` is a class. | None. |
| 12 | `LikeIgnoreAsciiCase` under BIN2 | hook | `LOWER` under BIN2 folds `É`→`é` and `İ`→`i` but not the Kelvin sign. `LikeIgnoreAsciiCaseBehaviourTests` × 9, kit Like × 4 and `BaseItemRepositorySortNameFilterTests` × 13 fail. | Translate as `TRANSLATE(x, 'A…Z', 'a…z') LIKE p ESCAPE '\'` (measured exact; ADR-0005 R13 entry criterion). With it, all pass except the two range rows (row 13). |
| 13 | Sort-name range filters with a non-ASCII term | gap | With the hook, `NameStartsWithOrGreater` and `NameLessThan` return exactly MySQL's lists. | Ledger entry `SQLSERVER-SORTNAME-RANGE-CASE` with the MySQL matcher (spike: `KnownGaps.SortNameRangeLowersNonAsciiOnSqlServer`), as R13 requires. |
| 14 | Trailing-space equality | gap | `Rock` and `Rock␠` collide in `IX_ItemValues_Type_Value` with Msg 2601 (probe); `N'Rock' = N'Rock '` under BIN2 and BIN2_UTF8. **No suite test covers it.** | A red-first test (B33) marked `ADR-0006/G1` on SQL Server. |
| 15 | UTF-16 ordering | gap | BIN2 on `nvarchar` orders `z, U+1F600, U+E000, U+FFFD`; code-point order (SQLite, BIN2_UTF8) is `z, U+E000, U+FFFD, U+1F600` (`semantics-*.txt`). **No suite test covers it.** | A red-first ordering test marked `ADR-0006/G2`. ADR-0012 (f) is the route that would close it. |
| 16 | `DateTime` precision and range | works; hook | `datetime2(7)` holds `MinValue` and `MaxValue`; kit DateTimeLimits 12/12, `DateTimeRoundTripTests` 26/26. **But** `OrderMapper.cs:69`'s `new DateTime(2000, …).AddYears(year − 2000)` becomes `DATEADD(year, …, '2000-01-01T00:00:00.000')`. That string literal is `datetime` (`SQL_VARIANT_PROPERTY`), so year 1 fails with Msg 517 and the whole PremiereDate sort fails (`BaseItemRepositoryPremiereDateOrderTests` × 1). | A provider `IMethodCallTranslatorPlugin` that types the constant as `datetime2` (`CAST('…' AS datetime2)`). It is a public extension point, registered through an options extension with scoped lifetime. With it, both PremiereDate tests pass. No shared change. |
| 17 | 2100 parameters | works | EF 10's default is `ParameterizedCollectionMode=MultipleParameters`; a plain `Contains` of 3 values sends 3 parameters, and one of 3000 falls back to a single `OPENJSON` parameter (probe). `EF.Parameter` is always `OPENJSON`. Kit IdList 8/8, 100,000 ids included. | None. Keep the `EF.Parameter` path, as planned. |
| 18 | EF compatibility level | decision | EF assumes `SqlServerCompatibilityLevel=150` (2019). New databases are at 160 on 2022 and 170 on 2025. | Pin the level in the provider; the spike measured only 150 (B33). |
| 19 | Explicit identity values | hook | Msg 544 *"Cannot insert explicit value for identity column … when IDENTITY_INSERT is set to OFF"*: `BackupServiceTests` × 34, kit Restore × 2. Only one table per session may have it (Msg 8107). After an explicit id of 10, the next generated id is 11. | `BeginExplicitIdentityInsertAsync` with `SET IDENTITY_INSERT [t] ON/OFF` on the open connection (ADR-0003 S2). Done in `c0b4bcc44b`. |
| 20 | Reseed and purge | works | `RESEED 0` on a never-used table gives id 0; after a `DELETE` it gives 1; `DBCC CHECKIDENT` rolls back with its transaction (`semantics-*.txt`). The stub reseeds only where `sys.identity_columns.last_value IS NOT NULL`. Kit Purge 8/8, in and outside a transaction. | Keep that rule (B15). A red-first test: a purge that reseeds every table gives a first id of 0 on a never-used one. |
| 21 | Isolation and RCSI | hook | A pooled connection reused after `SET … SERIALIZABLE`, or after `BeginTransaction(Serializable)`, is still SERIALIZABLE on both servers. With `ReadCommittedConnection` (an unnamed level becomes READ COMMITTED, as in MySQL's), an EF transaction on such a connection runs at READ COMMITTED, and so does the session after it. **A statement outside a transaction on the reused connection still runs SERIALIZABLE** (`probes-c3-*`). So the remedy covers EF transactions and saves only. The migrations leave READ COMMITTED, and production code names no level. | `ReadCommittedConnection`, plus a reset of the level for statements outside a transaction: set READ COMMITTED when a connection opens, as the MySQL session interceptor does. Also the RCSI warning of ADR-0008 R3, and the ADR-0008 amendment. The test databases are created with RCSI on. |
| 22 | Retryable errors | hook | Measured numbers: unique index 2601, primary key 2627, deadlock victim 1205, lock time-out 1222; foreign key 547 and truncation 2628 are not retryable. Kit WriteConflict fails × 2 without a classifier. | Classify 2627, 2601, 1205, 1222 and 3960 (ADR-0008 R2). 3960 occurs only under SNAPSHOT isolation, which nothing uses; it is not measured. Done in `c0b4bcc44b`. |
| 23 | Transactional DDL | works | A `CREATE TABLE` in a rolled-back transaction is gone; the failing-migration path is the PostgreSQL one (ADR-0003 S1). | `SupportsTransactionalMigrations` stays `true`. |
| 24 | DatePlayed and `MAX` over a union | works | SQL Server resolves the outer column inside the `UNION ALL` derived table (probe "Max over a union"); `BaseItemRepositoryDatePlayedOrderTests` 4/4. | None; MariaDB's interceptor is not needed. |
| 25 | Raw SQL in shared code | works | The only JF0002 suppressions run either history-repository scripts (the engine's own SQL: `JellyfinMigrationService.cs:129,182,750`, `BackupService.cs:210,223`) or SQLite-only routines. No new hook. | None. With the provider referenced, the two SQL Server entries of `DatabaseBannedSymbols.txt` now resolve in `Jellyfin.Server.Implementations`, and the build stays clean. |
| 26 | Model conformance | test | Kit `Model_OfTheSelectedProvider_HasTheShapeOfTheSqliteModel` reports the two `NoAction` keys and the unfiltered `(TopParentId, Type, IsVirtualItem)` index. | Two exact allowance sets in `EngineTraits` instead of the one bool (B33). |
| 27 | Supply chain | decision | The first restore of the stub reported NU1902 and NU1903 for `System.Security.Cryptography.Pkcs` 9.0.11, which `Microsoft.Data.SqlClient` 6.1.6 brings; that output was not saved. With the pin to 10.0.13, `dotnet list package --vulnerable --include-transitive` reports nothing at the tip, and it lists `Azure.Identity` 1.17.1 and `Microsoft.Identity.Client` 4.84.2 (`evidence/vulnerable-packages-tip.txt`). | Keep the `Pkcs` pin. Step 27's reviewer runs `dotnet list package --vulnerable --include-transitive` at the tip. Review the SqlClient tree as ADR-0011 R1 did for MySQL. |
| 28 | Collation of the database | decision | The test databases are created `COLLATE Latin1_General_100_BIN2`. A database that an operator creates has the server default (`SQL_Latin1_General_CP1_CI_AS` on both images), which the history table and any untyped literal comparison would use. A BIN2 database default also makes catalog names case-sensitive. | A C8 counterpart: BIN2 on the history repository, and a startup check of the default that warns. The baseline does not `ALTER DATABASE … COLLATE`, which needs exclusive access. |
| 29 | Test-side portability | test | <ul><li>`PoolChecks.cs:78` reads `COUNT(*)` as `long` (`InvalidCastException`, × 2).</li><li>The failed-write interceptor of `DisplayPreferencesManagerFailedWriteTests` strips `"` and backticks but not `[ ]` (× 1).</li><li>`ManyServiceProvidersCreatedWarning` (row above).</li><li>The engine switches in `StartOverTests` and `JellyfinApplicationFactoryTests`.</li></ul> | <ul><li>A count statement in `PoolStatements`.</li><li>Match the table through `ISqlGenerationHelper`.</li><li>The offline model tests ignore the warning.</li><li>`EngineTraits` for the integration switches.</li></ul> |
| 30 | Session defaults | works | `arithabort=0`, `ansi_warnings=1`, `ansi_nulls=1`, `concat_null_yields_null=1`, `lock_timeout=-1`, `us_english`, `mdy` (probe). | None. |
| 31 | One over-long row and the batch | shared | One `SaveItems` call saves a folder's new items in one transaction. With 50 new movies and one over-long `Path`, none is saved on SQL Server (Msg 1946), on PostgreSQL (`54000`, ADR-0009 G1) or on SQLite (a trigger raising an error), and all 50 on MySQL. The scan then skips that folder's subfolders and metadata refresh on every pass (`evidence/batch-save/`; ADR-0012). | ADR-0012: (S), save item by item after a failed batch, as the independent shared step X11 (B33), or (b) for `Path` in the provider if X11 is not accepted. |

## SQL Server 2025

- **The image.** `mcr.microsoft.com/mssql/server:2025-CU9-GDR1-ubuntu-24.04`, digest `sha256:742481d1b1186cccc81220a1eaacef244ca8d0f7d19c8e5c70df8ae541ffbab2`. It is the tag `2025-latest` points at; there is no CU10 tag (registry lookup, `evidence/registry-digests.txt`). It reports 17.0.5015.4 and *Enterprise Developer Edition* for `MSSQL_PID=Developer`.
- **Support status, from Microsoft's pages** (`evidence/sources-2025.txt`).
  - Lifecycle: start 18 November 2025, mainstream support to 7 January 2031, extended to 7 January 2036 (<https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2025>).
  - Build list: CU9 + GDR is 17.0.5015.4, KB5126967, 13 October 2026 (<https://support.microsoft.com/servicing/sql/sql-server-2025/kb5005684-build-versions>).
  - Linux: Ubuntu 24.04 and 22.04 are supported platforms (<https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-release-notes>).
  - For comparison, 2022's mainstream support ends on 12 January 2028 (<https://learn.microsoft.com/en-us/lifecycle/products/sql-server-2022>).
- **Differences from 2022.**
  - New databases get compatibility level 170, against 160.
  - The edition string differs.
  - Nothing else was found.
    - The stub runs differ only by the order-dependent warning and the probe artefact described under "The runs".
    - Every key, collation, `LIKE`, identity and isolation probe gives the same result.
    - The hook runs differ in 9 Implementations tests and in nothing else. Every one of them is that warning on another test: `ConnectionStringOverrideTests` × 2 on 2022, and seven offline MySQL and MariaDB model tests on 2025. Server.Tests and Integration agree test for test.
- **2022 stays the floor** (ADR-0011).

## Corrections to earlier records

- **ADR-0007 G1.** `MIN` over `uniqueidentifier` works on 2022 CU27 and 2025. The decision G2 stands, because the order differs.
- **ADR-0008 R3.** On SQL Server, a pooled connection keeps the isolation level a previous user set, so the provider must start its transactions at READ COMMITTED explicitly, as S5 did for MySQL.
- **ADR-0006 C3 and ADR-0009 L3** are confirmed as measured: trailing spaces (G1), UTF-16 order (G2), and the 1700-byte failure (`ADR-0009/G2`, now ADR-0012).
- **ADR-0001 M3 to M6** are confirmed on both servers: filters, identifier length, cascades and unique NULLs.

## Not covered by the spike

- A benchmark (ADR-0005 R1).
- A real library's lengths.
- Msg 3960.
- The RCSI-off warning path.
- A database created by an operator with the server's default collation.
- The restore-fails-closed test of ADR-0012.
- `MigrationBackupFast` and the statistics members.
- Any database other than one created by the test backend.
- The batch effect at scan level. It was measured at `SaveItems`; the scan-level consequences come from reading `Folder.cs` and `LimitedConcurrencyLibraryScheduler.cs`.
- The `2022-CU27-GDR1-ubuntu-22.04` image that B33 proposes to pin. The spike ran on the pre-GDR `2022-CU27-ubuntu-22.04`.

## Evidence index (`/tmp/sqlserver-spike/`)

- **`scripts/`:** `gate.sh`, `suite.sh`, `probes.sh`, `run1.sh` to `run3.sh`, `trx.py`, `keys.py`.
- **`gates/`:** per-commit gates and the base Debug warning set.
- **`evidence/run1-2022`, `run2-2025`, `run3-2022`, `run3-2025`, `four-engines`:** TRX files, logs, summaries (`*.summary.txt` lists every failure with its first message), identities and docker snapshots.
- **`evidence/probes-*`:** probe outputs.
- **`evidence/adr0012-create-warnings-*.txt`:** the 18 warnings.
- **`evidence/adr0012-keys-*.txt`:** thresholds, and options (b), (c) and (e). Section (f) there is an early probe that converted through the database's Latin1 code page. Its CJK and emoji rows are void, and `adr0012-utf8.txt` supersedes them; the ASCII and `é` rows stand.
- **`evidence/adr0012-utf8.txt`:** UTF-8 bytes per character; the 566/567 CJK and 425/426 emoji thresholds; the 1500 CJK characters that `varchar(4000)` rejects with Msg 2628; the `CAST` code page.
- **`evidence/adr0012-utf8-plans.txt`:** seek and scan by parameter type.
- **`evidence/adr0012-hash-plans-2022.txt`:** option (b), scan without the hash conjunct and seek with it.
- **`evidence/batch-save/`:** the batch measurement on SQL Server, PostgreSQL, SQLite and MySQL (`summary.txt`, TRX, and the scratch test itself, which is not committed).
- **`evidence/vulnerable-packages-tip.txt`:** the package scan at the spike tip.
- **`evidence/adr0012-index-table.tsv`:** the per-index table.
- **`evidence/model-creation-errors.txt`:** Msg 1785, 156 and 1919.
- **`evidence/semantics-*.txt`:** order, trailing spaces, `LIKE`, `LOWER` and `TRANSLATE`, Guid order, `RESEED`, `IDENTITY_INSERT`, DDL.
- **`evidence/dateadd-literal-2022.txt`:** Msg 517 and the `datetime2` fix.
- **`evidence/quoted-identifier.txt`:** Msg 10735.
- **`evidence/sources-2025.txt` and `registry-digests.txt`:** the 2025 image and its support status.
- **`probe/`:** the SQL probes and the stub's scripted DDL.
