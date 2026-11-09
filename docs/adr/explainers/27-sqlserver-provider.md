# Explainer: step 27, the SQL Server database provider (and step 27b, connection encryption)

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branches:**
  - `multidb/27-sqlserver-provider`: 9 code commits on `fc39749cf9` (`c0886933d2..d6d0951e9c`), plus documentation commit `27e938f529`.
    - `c0886933d2` Keep the offline model tests out of EF Core's service provider cache
    - `d7f5fe0b3e` Name in the traits of an engine exactly where its model may differ
    - `db4884f8c0` Add a SQL Server database provider, not registered yet
    - `314186a18d` Run the database tests on SQL Server
    - `e205789a93` Pin the sessions and the startup checks of SQL Server
    - `f07589f3eb` Classify the errors SQL Server really raises
    - `3349e02100` Pin the identity columns of SQL Server around a purge and explicit ids
    - `aac5628e4b` Pin keys over 1700 bytes on SQL Server as a known gap
    - `d6d0951e9c` Pin trailing spaces and UTF-16 order on SQL Server as known gaps
  - `multidb/27b-sqlserver-encrypt`: 1 code commit on `c467a21683` (`de82cc8c29`), plus documentation commit `c807e35cff`.
    - `de82cc8c29` State that SQL Server connections are encrypted unless told otherwise
- **Size:**
  - Step 27: 77 files, +4995/-158 lines. Production code is about 1,200 lines across 11 files in `src/Jellyfin.Database.Providers.SqlServer/`; the remainder is test infrastructure, conformance tests, and engine traits.
  - Step 27b: 2 files, +88/-4 lines (`SqlServerDatabaseProvider.cs` and `SqlServerDatabaseProviderTests.cs`).
- **Status:**
  - Reviewed across multiple rounds (backups `multidb/27-sqlserver-provider-backup-a` through `-l`, `multidb/27b-sqlserver-encrypt-backup-a`).
  - Red controls 22/22 caught and reverted clean (`/tmp/27/red/final-k/results.json`, `COMPLETE`).
  - Per-commit gates `g11` 163/163 rc 0 across 126 test legs, 24,215 TRX rows with 0 failures under a 12G memory cap (`/tmp/27/gates/g11/results.tsv`, `COMPLETE`).
  - Full suite `s10` on SQL Server 2022 CU27-GDR1 (16.0.4305) with 0 failures across all test suites, and advisory SQL Server 2025 CU9-GDR1 (17.0.5015) with identical 0 failures (`/tmp/27/dev/s10/`, `COMPLETE`).
- **Relevant decisions:** ADR-0001 (M5, M6, E3), ADR-0006 C9/G1/G2 (collation, trailing spaces, UTF-16 ordering), ADR-0008 S7/S8/S9 (isolation levels, connection reset, removal of `ReadCommittedConnection`, `Pessimistic` refusal), ADR-0009 G2 (1700-byte index limit), ADR-0011 F9/F10 (SQL Server >= 2022, Azure refusals, encryption default), ADR-0012 (text-class index keys, decision (a) + (S) via X11 fallback), pr-plan B37 and B39.
- **The provider is not registered and has no migrations.** With this step, `Jellyfin.Database.Providers.SqlServer` implements `IJellyfinDatabaseProvider` for SQL Server 2022+ and SQL Server 2025. It is not registered in DI (`ServiceCollectionExtensions.cs` does not reference it yet) and ships no migrations; step 28 adds the baseline migration and registration.

## Why it exists

ADR-0011 lists Microsoft SQL Server 2022 (major version 16.0) as an Experimental database target. Following the exploratory spike (ADR-0001 E3, `docs/adr/explainers/sqlserver-spike.md`) and the decision on index key lengths (ADR-0012), Step 27 implements the core SQL Server provider project, model conventions, query expression translators, error classification, session interceptors, and cross-engine testing harness integration.

Like Step 24 for MySQL, Step 27 separates provider implementation from registration and migrations. Tests run against schemas generated directly from the model using the leased database pool (`SqlServerTestBackend`), verifying conventions and query translations without depending on EF Core migrations scaffolding.

Step 27b (`multidb/27b-sqlserver-encrypt`) hardens the provider's connection string parsing by explicitly setting `Encrypt=True` (Mandatory) whenever the connection string does not specify encryption, documenting the false-positive nature of CodeQL alerts #63 and #64.

## What each commit does

### Step 27

- **`c0886933d2` Keep the offline model tests out of EF Core's service provider cache.**
  - Fixes the order-dependent `ManyServiceProvidersCreatedWarning` where EF Core threw an exception once 20 internal service providers were cached across the test process.
  - Offline model contexts in `OfflineContexts.cs` explicitly disable caching via `builder.EnableServiceProviderCaching(false)`.
  - Adds `InternalServiceProviderTests` to verify that the runtime engine's test contexts share a single internal service provider across databases and interceptors.
- **`d7f5fe0b3e` Name in the traits of an engine exactly where its model may differ.**
  - Replaces the broad `SupportsPartialIndexes` boolean with structured `ModelAllowances` (`UnexpressibleIndexFilters`, `NoActionCascades`, `NullFilteredUniqueIndexes`) in `EngineTraits`.
  - Defines the exact structural differences permitted for SQL Server, MySQL, and MariaDB compared to the SQLite reference model.
- **`db4884f8c0` Add a SQL Server database provider, not registered yet.**
  - Creates `src/Jellyfin.Database/Jellyfin.Database.Providers.SqlServer/` targeting .NET 9 and referencing `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12.
  - Implements `SqlServerDatabaseProvider` with conventions:
    - Big-endian `binary(16)` Guids (`BigEndianGuidConverter`).
    - Binary text collation `Latin1_General_100_BIN2` (`TextCollationConvention` and `BinaryCollationHistoryRepository`).
    - Text-class strings bounded at `nvarchar(4000)` (`IndexedTextConvention`), and unindexed text as `nvarchar(max)` (`UnindexedTextConvention`).
    - Removal of `OR` filters in non-unique indexes (`IndexFilterConvention`).
    - `DeleteBehavior.NoAction` on `BaseItems.ParentId` and `AncestorIds.ParentItemId` (`CascadePathConvention`).
    - Max identifier length of 128 (`IdentifierLengthConvention`).
    - `TRANSLATE`-based ASCII folding for `LikeIgnoreAsciiCase` (`AsciiCaseInsensitiveLike`).
    - `DateTimeConstantTranslator` casting date literals to `datetime2` to avoid `DATEADD` 1753 underflows.
- **`314186a18d` Run the database tests on SQL Server.**
  - Introduces `SqlServerServer` and `SqlServerTestBackend` in `tests/Jellyfin.Database.Testing/`, integrating SQL Server 2022 container lifecycle into the leased test pool.
  - Enrolls SQL Server into `EngineTraits`, `PoolStatements`, and the 64-row cross-engine conformance kit.
  - Adds `SchemaMigrationsFactAttribute` and `SchemaMigrationsTheoryAttribute` to skip migration-dependent tests until Step 28.
- **`e205789a93` Pin the sessions and the startup checks of SQL Server.**
  - Implements `SessionSettingsInterceptor`, running `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` whenever a connection opens to clean pooled connections.
  - Enforces startup validation in `SessionSettingsInterceptor`: refuses Azure SQL Database (edition 5), Azure Synapse (6 and 11), Azure SQL Managed Instance (8), and servers older than major version 16 (2022).
  - Adds startup checks in `ApplyDatabaseSettingsAsync` verifying `READ_COMMITTED_SNAPSHOT ON` (RCSI) and `Latin1_General_100_BIN2` collation, logging warnings if misconfigured.
  - Pins session options: `QUOTED_IDENTIFIER`, `ANSI_WARNINGS`, `ANSI_NULLS`, `CONCAT_NULL_YIELDS_NULL`.
- **`f07589f3eb` Classify the errors SQL Server really raises.**
  - Implements `IsRetryableWriteConflict`: deadlocks (1205), lock timeouts (1222), duplicate keys (2601, 2627), and snapshot update conflicts (3960).
  - Implements `IsDataError`: string truncations (2628, 8152), index key overflow (1946), check/foreign key violations (547), null violations (515), numeric overflows (8115), conversion failures (245, 8114), and user errors (>= 50000). Filters out transient errors (-2, 1205, 1222, 3960).
  - Inspects every error in `SqlException.Errors` to find root causes beyond generic Msg 3621 ("The statement has been terminated").
- **`3349e02100` Pin the identity columns of SQL Server around a purge and explicit ids.**
  - Implements `BeginExplicitIdentityInsertAsync` scoping `SET IDENTITY_INSERT <table> ON/OFF` on dedicated connections.
  - Implements `PurgeDatabase` disabling foreign keys (`NOCHECK CONSTRAINT ALL`), deleting table rows, re-enabling and validating keys (`WITH CHECK CHECK CONSTRAINT ALL`), and conditionally reseeding identities with `DBCC CHECKIDENT(<table>, RESEED, 0)` only where `last_value IS NOT NULL`.
- **`aac5628e4b` Pin keys over 1700 bytes on SQL Server as a known gap.**
  - Adds `KnownGaps.IndexKeyOverTheLimitOnSqlServer` (`SQLSERVER-INDEX-KEY-1700`) matching Msg 1946.
  - Adds `KnownGaps.PathOverTheColumnOnSqlServer` (`SQLSERVER-PATH-OVER-4000`) matching Msg 2628.
  - Tests threshold limits: 850 code units pass, 851 fail Msg 1946; composite key thresholds; batch save rollback; and backup restore rollback.
- **`d6d0951e9c` Pin trailing spaces and UTF-16 order on SQL Server as known gaps.**
  - Adds `KnownGaps.TrailingSpacesOnSqlServer` (`SQLSERVER-TRAILING-SPACE`): SQL Server pads trailing spaces during equality comparisons under all collations, colliding on `IX_ItemValues_Type_Value` (Msg 2601).
  - Adds `KnownGaps.Utf16OrderOnSqlServer` (`SQLSERVER-UTF16-ORDER`): binary collations on SQL Server sort UTF-16 code units rather than Unicode code points, causing supplementary characters (e.g. U+1F600) to sort before high-BMP characters (U+E000).

### Step 27b

- **`de82cc8c29` State that SQL Server connections are encrypted unless told otherwise.**
  - Updates `SqlServerDatabaseProvider.ParseConnectionString` to ensure `Encrypt=True` (Mandatory) is set if the operator's connection string does not specify `Encrypt`.
  - Preserves any explicit operator setting (`Encrypt=False`, `Optional`, `Strict`).
  - Documents the CodeQL `cs/insecure-sql-connection` (alerts #63 and #64) false positive: CodeQL only accepts boolean object initializers (`Encrypt = true`), which SqlClient 6's enum `SqlConnectionEncryptOption.Mandatory` does not match and which would override operator intent.

---

## Root-causing and fixing `ManyServiceProvidersCreatedWarning`

EF Core caches internal service providers keyed by the combination of `DbContextOptions` used in a process. It caps this cache at 20 distinct configurations; requesting a 21st configuration causes EF Core to throw `ManyServiceProvidersCreatedWarning` (which defaults to `Throw` in test harnesses).

### The Problem
During test runs of `Jellyfin.Server.Implementations.Tests`:
1. One internal service provider was cached for the active test engine (e.g., SQLite or MySQL).
2. 18 internal service providers were created by offline model tests (`OfflineContexts.cs`), which deliberately instantiate contexts across multiple server versions, providers, and collation probes.
3. Total configurations cached reached 19.
4. When SQL Server tests introduced their own distinct configurations, the count exceeded 20. EF Core threw the warning on the 21st context, and since failed builds are not cached, all subsequent tests using that configuration failed (37 tests failed in the initial spike run).

### The Fix
In `OfflineContexts.cs`, offline contexts do not need caching across queries:
```csharp
private static JellyfinDbContext Create(DbContextOptionsBuilder<JellyfinDbContext> builder, IJellyfinDatabaseProvider provider)
{
    builder.EnableServiceProviderCaching(false);
    return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
```
Uncached service provider builds are checked against the count but not added to the process cache. This dropped the process cache count to 9 configurations at exit.

To ensure production and test contexts for the active database engine do not inadvertently leak options configurations, `InternalServiceProviderTests` was added:
```csharp
[Fact]
public void Contexts_OnTwoDatabasesOfTheSelectedEngine_ShareOneInternalServiceProvider()
{
    var backend = TestDatabaseEnvironment.Backend;
    using var firstDatabase = backend.Create(TestSchemaSource.Model, []);
    using var secondDatabase = backend.Create(TestSchemaSource.Model, [new NoOpCommandInterceptor()]);
    using var first = CreateContext(firstDatabase);
    using var second = CreateContext(secondDatabase);

    Assert.False(AreTheSameDatabase(firstDatabase, secondDatabase));
    Assert.Same(first.GetService<IModelSource>(), second.GetService<IModelSource>());
}
```
This guarantees that regardless of database instance or command interceptors, contexts on the selected engine reuse the identical service provider.

---

## `ModelAllowances` in `EngineTraits`

Earlier steps represented model differences through a coarse `SupportsPartialIndexes` boolean. This worked when only MySQL/MariaDB lacked partial indexes, but failed for SQL Server, which supports partial indexes but restricts certain expressions, disallows cyclic cascades, and treats NULLs in unique indexes differently.

`ModelAllowances` defines three exact sets:
1. `UnexpressibleIndexFilters`: Exact filter strings of non-unique indexes that the engine drops, making the index cover all rows.
2. `NoActionCascades`: Foreign keys `(Table, Columns)` where the SQLite model specifies `Cascade` but the engine must use `NoAction` to avoid engine cascade errors.
3. `NullFilteredUniqueIndexes`: Unique indexes `(Table, Columns, Filter)` where the engine filters out NULLs so that multiple NULL values can exist.

### Allowed Model Differences by Engine

| Engine | `UnexpressibleIndexFilters` | `NoActionCascades` | `NullFilteredUniqueIndexes` |
|---|---|---|---|
| **SQLite** | None (`[]`) | None (`[]`) | None (`[]`) |
| **PostgreSQL** | None (`[]`) | None (`[]`) | None (`[]`) |
| **MySQL / MariaDB** | 4 filters (all partial filters dropped) | None (`[]`) | None (`[]`) |
| **SQL Server** | `PrimaryVersionFilter` (1 filter) | `("BaseItems", "ParentId")`<br>`("AncestorIds", "ParentItemId")` | `("ImageInfos", "UserId", "[UserId] IS NOT NULL")` |

Every allowance must correspond to an actual model difference; an unused allowance or an unlisted difference fails model conformance tests.

---

## `SqlServerDatabaseProvider` Conventions

The provider configures the model so that shared entity definitions run seamlessly on SQL Server without engine-specific entity code:

1. **Big-Endian Guids (`BigEndianGuidConverter`):**
   - SQL Server's native `uniqueidentifier` type orders bytes abnormally: it sorts the last 6 bytes first (historical COM GUID ordering).
   - This causes `MIN(Id)`, grouping, and paging to produce completely different results on SQL Server compared to other engines.
   - The provider maps `Guid` to `binary(16)` using `BigEndianGuidConverter` (`Guid.ToByteArray(bigEndian: true)`), ensuring byte order matches canonical string representations.
2. **Binary Collation (`Latin1_General_100_BIN2`):**
   - SQL Server default database collations are case-insensitive and accent-insensitive.
   - `TextCollationConvention` traverses all text columns (`char`, `varchar`, `text`, `nchar`, `nvarchar`, `ntext`) and assigns `Latin1_General_100_BIN2`.
   - `BinaryCollationHistoryRepository` subclasses `SqlServerHistoryRepository` to assign `Latin1_General_100_BIN2` to `__EFMigrationsHistory` (`MigrationId` and `ProductVersion`).
3. **Text Column Bounds (`IndexedTextConvention` & `UnindexedTextConvention`):**
   - Unbounded indexed text (`IsUnboundedIndexedText()`) is mapped to `nvarchar(4000)` because `nvarchar(max)` cannot participate in indexes (Msg 1919).
   - Text properties that have a `MaxLength` but are *not* part of any key, foreign key, or index are mapped to `nvarchar(max)`. This ensures values that other engines accept are not rejected by arbitrary column limits.
4. **Dropping `OR` Index Filters (`IndexFilterConvention`):**
   - SQL Server filtered indexes only allow simple conjunctions (`AND`), forbidding `OR` expressions (Msg 10735).
   - `IX_BaseItems_Id_Type_IsFolder_IsVirtualItem` contains `[Id] = [PresentationUniqueKey] OR [Type] = 'Folder' ...`.
   - `IndexFilterConvention` identifies `OR` keywords via regex and strips the filter from non-unique indexes, allowing them to index all rows. It throws if an `OR` filter is encountered on a unique index.
5. **Cascading Foreign Keys (`CascadePathConvention`):**
   - SQL Server raises Msg 1785 if multiple cascade paths exist to a table or if a self-referencing relationship cascades on delete.
   - `BaseItems.ParentId` (self-referencing) and `AncestorIds.ParentItemId` (multiple cascade paths to `BaseItems`) are assigned `DeleteBehavior.NoAction`.
   - Application-level deletions in Jellyfin handle cleaning up children and ancestor rows.
6. **Identifier Lengths (`IdentifierLengthConvention(128)`):**
   - Restricts table, column, and constraint names to 128 characters.
7. **DateAdd Translation (`DateTimeConstantTranslator`):**
   - In SQL Server, `DATEADD` with a string literal infers the `datetime` type, which cannot represent years prior to 1753 (Msg 517).
   - `DateTimeConstantTranslator` translates `DateTime.AddYears`, `AddMonths`, etc., by casting the constant literal explicitly to `datetime2`.
8. **Case-Insensitive LIKE (`AsciiCaseInsensitiveLike`):**
   - Under `Latin1_General_100_BIN2`, `LIKE` is strictly case-sensitive.
   - SQL Server's `LOWER()` function folds non-ASCII characters (e.g. `É` to `é`), violating Jellyfin's ASCII-only case insensitivity contract.
   - `AsciiCaseInsensitiveLike` translates `JellyfinDbFunctions.LikeIgnoreAsciiCase` using `TRANSLATE(COLLATE(value, 'Latin1_General_100_BIN2'), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')`.

---

## Rework: Removing `ReadCommittedConnection`

### The Initial Hypothesis and Rework Cause
During initial development, a custom `ReadCommittedConnection` (inheriting EF Core's `ISqlServerConnection`) was introduced, mirroring the MySQL provider's approach. It intercepted `BeginTransaction` to convert `IsolationLevel.Unspecified` to `ReadCommitted`, believing that SqlClient would otherwise leave unnamed transactions running at the connection's ambient session isolation level.

However, during red control testing at commit `b9f6915197`, removing `ReadCommittedConnection` did not fail any test:
- **Red control `isolation-connection`: NOT CAUGHT.**

### Diagnostic Findings
Investigation into `Microsoft.Data.SqlClient` 6.1.6 revealed:
- `SqlInternalConnection.BeginSqlTransaction` explicitly converts `IsolationLevel.Unspecified` to `ReadCommitted` before sending the transaction start command to SQL Server:
  ```csharp
  // Microsoft.Data.SqlClient / SqlInternalConnection.cs (v6.1.6, lines 239-242)
  if (isolationLevel == IsolationLevel.Unspecified) {
      isolationLevel = IsolationLevel.ReadCommitted;
  }
  ```
- Diagnostics confirmed that even if a pooled connection had previously executed `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE`, any subsequent unnamed transaction started by EF Core or ADO.NET automatically executes at `READ COMMITTED`.
- Therefore, `ReadCommittedConnection` was completely redundant and relied on an internal EF Core API (`EF1001`).

### What Was Done
1. `ReadCommittedConnection.cs` and its registration were removed from the SQL Server provider (`db4884f8c0`).
2. `SessionSettingsInterceptor` was retained: it runs `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` at connection open. This ensures statements executed *outside* of a transaction on a pooled connection run at READ COMMITTED.
3. Two new test theories were added to `SqlServerSessionTestsBase` (executed by `SqlServerSessionTests` and `SqlServerLockingReadsSessionTests`) to continuously pin SqlClient's unnamed transaction behavior.

---

## Startup Checks & Security

### Engine Edition & Version Refusals
In `SessionSettingsInterceptor`, the server's edition and version are checked when the first connection opens:
- Refuses Azure cloud services by querying `SERVERPROPERTY('EngineEdition')`:
  - Azure SQL Database (Edition 5)
  - Azure Synapse Dedicated Pool (Edition 6)
  - Azure SQL Managed Instance (Edition 8)
  - Azure Synapse Serverless Pool (Edition 11)
  *Rationale:* Azure SQL Database always reports version 12 regardless of compatibility level, and Managed Instance updates dynamically; edition checks prevent misleading version errors.
- Refuses on-premises SQL Server instances older than major version 16 (SQL Server 2022).

### Runtime Configuration Warnings
In `ApplyDatabaseSettingsAsync`:
- Checks `is_read_committed_snapshot_on`: Logs a Warning if RCSI is off, instructing the administrator to run `ALTER DATABASE <db> SET READ_COMMITTED_SNAPSHOT ON` to prevent read queries from blocking scan writes.
- Checks `DATABASEPROPERTYEX(DB_NAME(), 'Collation')`: Logs a Warning if the default database collation is not `Latin1_General_100_BIN2`.
- Queries are executed as individual scalar queries to prevent catalog collation conflicts.

### Mandatory Encryption (Step 27b)
`SqlServerDatabaseProvider.ParseConnectionString` enforces `Encrypt=True` (Mandatory) if `Encrypt` is omitted from the connection string:
- Matches SqlClient 6's default while ensuring transparency in logged connection strings (passwords stripped).
- Respects explicit operator settings (`Encrypt=False`, `Optional`, `Strict`).
- **CodeQL False Positive:** CodeQL `cs/insecure-sql-connection` (alerts #63 and #64) flags the builder because it does not use the syntax `new() { Encrypt = true }`. This is documented as a false positive (ADR-0011 F10) because `SqlConnectionEncryptOption.Mandatory` is an enum object and overriding the operator's choice is unacceptable.

---

## Known Gaps

Five known gaps are documented in `KnownGaps.cs` for SQL Server:

1. **`SQLSERVER-SORTNAME-RANGE-CASE`:** Non-ASCII range predicates under binary collation.
2. **`SQLSERVER-INDEX-KEY-1700`:** SQL Server rejects index keys exceeding 1700 bytes on nonclustered indexes (Msg 1946). 18 composite indexes on `BaseItems` can theoretically exceed this budget if text fields are large (ADR-0012).
3. **`SQLSERVER-PATH-OVER-4000`:** Paths longer than 4000 characters exceed the `nvarchar(4000)` column definition and fail with Msg 2628.
4. **`SQLSERVER-TRAILING-SPACE`:** SQL Server pads strings with spaces before comparing under all collations (ANSI specification). Two item values differing only by trailing spaces collide on `IX_ItemValues_Type_Value` with Msg 2601.
5. **`SQLSERVER-UTF16-ORDER`:** SQL Server binary collations sort UTF-16 code units rather than Unicode code points. Characters outside the BMP (e.g. U+1F600) sort before U+E000.

---

## Test Evidence, Gates, and Results

### Red Controls (`/tmp/27/red/final-k/results.json`)
22 red controls were tested and verified caught:
- `pessimistic-async`: Verifies that asynchronous saves under `PessimisticLockBehavior` hang (caught by 15-minute blame-hang timeout).
- `guid-converter`: Verifies big-endian Guid ordering.
- `bin2-collation`: Verifies `Latin1_General_100_BIN2` collation enforcement.
- `indexed-text`: Verifies `nvarchar(4000)` on unbounded indexed text.
- `unindexed-text`: Verifies `nvarchar(max)` on unindexed bounded text.
- `or-filter`: Verifies removal of unexpressible `OR` index filters.
- `cascades`: Verifies `DeleteBehavior.NoAction` on circular foreign keys.
- `like-translate`: Verifies ASCII translation in `LikeIgnoreAsciiCase`.
- `dateadd-literal`: Verifies `datetime2` casting in `DATEADD`.
- `identity-insert`: Verifies scoping of `IDENTITY_INSERT`.
- `reseed-always`: Verifies identity reseed logic on empty vs populated tables.
- `isolation-session`: Verifies session reset to `READ COMMITTED`.
- `retry-locks`: Verifies classification of write conflicts.
- `data-error-1946`: Verifies classification of 1700-byte key errors.
- `restore-commit-per-table`: Verifies restore atomicity.
- `pool-trust-in-fingerprint`: Verifies foreign key trust accounting in the pool.
- `purge-no-recheck`: Verifies constraint re-checking during purge.
- `registered`: Verifies that the provider remains unregistered.
- `gap-trailing-space`: Verifies `SQLSERVER-TRAILING-SPACE` gap matcher.
- `gap-utf16`: Verifies `SQLSERVER-UTF16-ORDER` gap matcher.
- `gap-1700`: Verifies `SQLSERVER-INDEX-KEY-1700` gap matcher.
- `collation-catalog-concat`: Verifies catalog query resilience against collation mismatches.

### Per-Commit Gates (`g11`)
- 163/163 gate rows passed rc 0 across base `fc39749cf9` and all 9 commits (3 n/a for SQL Server tests prior to backend introduction).
- Debug build: 0 errors, 211 warnings (identical to base).
- Release build: 0 errors, 0 warnings.
- Format check and docs render check: 0 changes.
- 126 test legs, 24,215 TRX rows: 22,552 Passed, 1,663 Skipped (expected provider skips), 0 Failed.

### Full Test Suite (`s10`)
Executed under 12G memory cap with 0 failures:
- **Implementations.Tests:** SQL Server (2,821 passed, 205 skipped, 0 failed), SQLite (2,865 / 169), PostgreSQL (2,863 / 149), MySQL (2,808 / 202), MariaDB (2,809 / 201).
- **Server.Tests:** SQL Server (106 / 64), SQLite (161 / 11), PostgreSQL (153 / 18), MySQL (131 / 40), MariaDB (131 / 40).
- **Server.Integration.Tests:** SQL Server (8 / 140 - integration tests skipped pending registration in Step 28), SQLite (137 / 11), PostgreSQL (144 / 6), MySQL (144 / 6), MariaDB (144 / 6).
- **Controller.Tests:** 218 passed, 0 failed across all runs.
- **SQL Server 2025 Advisory Run:** SQL Server 2025 CU9-GDR1 passed with identical counts (2,821 / 106 / 8 passed, 0 failed).

### Incidents Encountered
1. **Out of Memory Incident:** A scratch red control looped without bound due to EF Core options key creation. Fixed with a 40-iteration bound and by running all subsequent scripts under `systemd-run --user --scope -q -p MemoryMax=12G -p MemorySwapMax=0`.
2. **Pessimistic Lock Hang:** `PessimisticLockBehavior` held a thread-affine `ReaderWriterLockSlim` across `await`, causing `SynchronizationLockException` and hanging async writers. This motivated Step X13 to refuse `Pessimistic` on all server engines.
