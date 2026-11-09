# Multi-Database Support: Overall Study Explainer

These are study notes for understanding and defending the complete multi-database implementation (`feature/multi-db` and the upstream PR series) in your own words. Do not paste them as PR text.

- **Scope:** Complete architectural transition of Jellyfin from SQLite-only to a pluggable, portable multi-database architecture supporting 5 relational database engines: **SQLite**, **PostgreSQL**, **MySQL**, **MariaDB**, and **Microsoft SQL Server**.
- **Delivery Strategy:** Built on the fork as an end-to-end verified stack with independent per-step review, per-commit test gates, negative controls, and cross-engine matrix validation. Prepared for linear upstream submission as a clean commit series on `upstream/master` (pr-plan S1–S4).
- **Core Design Rule:** Zero runtime engine switches in shared server and query code. Engine-specific behaviors, types, collations, and conventions reside strictly inside provider assemblies, enforced at build time by custom Roslyn analyzers (`JF0002`/`JF0003`) banning unportable symbols and raw SQL.

---

## 1. Architecture Overview

### Provider Abstraction & Registration
Database engines plug into Jellyfin through `IJellyfinDatabaseProvider` (`src/Jellyfin.Database/IJellyfinDatabaseProvider.cs`). Rather than leaking provider-specific abstractions, the interface exposes uniform lifecycle and capability hooks:

- **`IsRetryableWriteConflict(Exception)`:** Classifies transient concurrency conflicts (deadlocks, lock wait timeouts, concurrency token mismatches, serialization failures) that should trigger an automatic retry:
  - SQLite: error code 5 (`SQLITE_BUSY`) and error code 6 (`SQLITE_LOCKED`).
  - PostgreSQL: SQLSTATE `40001` (serialization failure) and `40P01` (deadlock detected).
  - MySQL / MariaDB: error 1213 (deadlock), 1205 (lock wait timeout), and MariaDB 1020 (`CheckRead` under snapshot isolation).
  - SQL Server: error 1205 (deadlock victim), 1222 (lock request timeout), 2601/2627 (unique constraint race), and 3960 (snapshot update conflict).
- **`IsDataError(Exception)`:** Distinguishes non-transient row/data constraint violations from transient operational errors:
  - Trigger for Step X11's per-item fallback (`SaveItemsSkippingFailures`).
  - Filters out client timeouts, lost connections, and socket exceptions.
- **`BeginExplicitIdentityInsertAsync(dbContext, entityType, cancellationToken)`:** Manages explicit insertion of database-generated identifiers during data migrations and full database restores:
  - SQL Server: Scopes `SET IDENTITY_INSERT <table> ON/OFF` on a dedicated open connection.
  - PostgreSQL: Manages sequences with `setval()` to prevent counter desynchronization.
  - MySQL / MariaDB: Manages auto-increment values.
  - SQLite: Natural support without special session modes.
- **`CompleteDatabaseRestoreAsync(dbContext, cancellationToken)`:** Performs post-restore housekeeping across all engines: re-enabling foreign keys, resetting identity/sequence counters to `MAX(Id)`, verifying constraints, and writing the final migration history row.
- **`SupportsTransactionalMigrations`:** Declares whether DDL is transactional:
  - `true` on SQLite, PostgreSQL, and SQL Server (migrations roll back cleanly on failure).
  - `false` on MySQL and MariaDB (DDL causes implicit commits, requiring the `migration-in-progress.json` guard file).
- **`SupportsPessimisticLocking`:** Declares whether the provider supports thread-affine locking:
  - `true` on SQLite (protects the single-writer database file).
  - `false` on PostgreSQL, MySQL, MariaDB, and SQL Server (Step X13 startup refusal prevents deadlocks).
- **`PurgeDatabase(dbContext, tableNames)`:** Atomically cleans tables and resets identities in test and maintenance scenarios without dropping tables or altering permissions.
- **`MigrationBackupFast(cancellationToken)`:** Fast snapshot capability for engines supporting atomic file copies or snapshots prior to migration.

#### Configuration & Selection
The active provider is determined via `database.xml` in the configuration directory:
```xml
<DatabaseConfigurationOptions>
    <DatabaseType>Jellyfin-PostgreSQL</DatabaseType>
    <CustomProviderOptions>
        <ConnectionString>Host=db.lan;Database=jellyfin;Username=jellyfin;Password=secret</ConnectionString>
    </CustomProviderOptions>
</DatabaseConfigurationOptions>
```
To support cloud, Docker, and Kubernetes environments without storing secrets in XML files, Step 12 added the **connection-string override**:
- Configuration key: `database:ConnectionString`
- Environment variable: `JELLYFIN_DATABASE__CONNECTIONSTRING`
Overrides `CustomProviderOptions.ConnectionString` while keeping the provider type specified in `database.xml`.

#### Registered Providers
- `Jellyfin-SQLite` (default): `Jellyfin.Database.Providers.Sqlite`
- `Jellyfin-PostgreSQL`: `Jellyfin.Database.Providers.Postgres`
- `Jellyfin-MySQL`: `Jellyfin.Database.Providers.MySql` (serves both MySQL 8.4+ and MariaDB 11.4+)
- `Jellyfin-SqlServer`: `Jellyfin.Database.Providers.SqlServer`
- Extensible via `PLUGIN_PROVIDER` for out-of-tree community plugins.

---

### Shared Model & Query Portability

The core entity model (`JellyfinDbContext`) is shared across all engines. Portability is achieved through conventions and model sanitization rather than conditional entity definitions:

1. **`JellyfinDbContext.SanitizeStrings` (Step 06):** Intercepts entity modifications before persistence and strips embedded NUL (`\0`) characters. PostgreSQL rejects NUL bytes in UTF-8 text strings with error `22021`, causing crashes during library scans; stripping them at the DbContext boundary protects all backends.
2. **`EnforceKeyLengths` (Step 19):** Validates and enforces maximum lengths for key-class strings (`[MaxLength]`), preventing database-level index size violations across different storage engines.
3. **`UtcDateTimeConverter` (Steps 02, 02b, 04):** Enforces universal UTC date-time storage. SQLite stores dates as ISO 8601 strings, PostgreSQL uses `timestamp with time zone`, MySQL uses `datetime(6)`, and SQL Server uses `datetime2(7)`. The converter ensures `DateTimeKind.Utc` is preserved across all providers and clamps `DateTime.MinValue` and `MaxValue` to sentinel values that roundtrip identically across all engines.
4. **`IdentifierLengthConvention`:** Automatically truncates index, key, and constraint identifiers to the database maximum (e.g., 64 characters for PostgreSQL and MySQL, 128 characters for SQL Server).
5. **`JellyfinDbFunctions.LikeIgnoreAsciiCase` & `LikePatterns` (Steps 05, 20b):**
   - Standard SQL `LIKE` and `LOWER()` implementations vary widely in how they treat Unicode characters (e.g., `ß` matching `SS`, `É` matching `e`, or Greek/Cyrillic foldings).
   - `LikeIgnoreAsciiCase` guarantees identical ASCII-only case insensitivity (`A-Z` folds to `a-z`) across every engine:
     - SQLite: custom SQLite user-defined function.
     - PostgreSQL: custom SQL function `jellyfin_like_ignore_ascii_case` or `translate()`.
     - MySQL / MariaDB: query-level translation.
     - SQL Server: `TRANSLATE(COLLATE(value, 'Latin1_General_100_BIN2'), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')`.
   - `LikePatterns` provides standard escaping for wildcard characters (`%`, `_`, and `[` on SQL Server).
6. **Normalized Match Columns (Step 20):**
   - Text sorting and searching in binary collations cannot seek on expressions without scanning.
   - Introduced `BaseItems.CleanOriginalTitle` and `Peoples.NameLower` as persisted, indexed columns populated at save time, enabling indexed seeks across all engines.
7. **`SaveItemsSkippingFailures` (Step X11):**
   - Media library scans batch up to 50 items in a single transaction. Under standard EF Core, a single invalid path or constraint failure in any item causes the entire batch to fail and roll back, stopping library scans dead.
   - X11 implements an engine-neutral fallback: if a batch fails on a non-transient data error (`IsDataError`), it retries saving the items individually in separate transactions, logs the failing item, and allows the remaining valid items to save successfully.

---

### Contributor Migration Workflow

Adding migrations across multiple database providers is automated and enforced by toolsets:

1. **`dev/add-migration <MigrationName>` (Step 22b, 28):**
   A developer runs a single CLI script to scaffold migrations across all providers simultaneously:
   - Evaluates the design-time model against each provider's snapshot.
   - Scaffolds migrations into `src/Jellyfin.Database.Providers.<Provider>/Migrations/`.
2. **`PairedMigrationsIdGenerator` (Step 22c, 28):**
   - Overrides EF Core's default timestamp generation so that all provider migrations generated for a change receive the exact same migration ID and timestamp (matching the SQLite reference migration).
3. **Automated Migration Validation Tests:**
   - **`MigrationParityTests`:** Asserts that every migration ID present in SQLite has an identical paired migration ID in PostgreSQL, MySQL, and SQL Server.
   - **`MigrationContentsTests` & `MigrationContentsAllowlist.json` (Step 22c):** Compares the AST operations of non-SQLite migrations against the reference SQLite migration. Any engine-specific divergence (such as prefix indexes, dropped filters, or cascade differences) must be explicitly justified in `MigrationContentsAllowlist.json` under strict categories (`BaselineCoverage`, `EngineSpecific`, `EquivalentSql`).
4. **Roslyn Analyzers `JF0002` & `JF0003` (Steps 22d, 27a):**
   - Built in `src/Jellyfin.Database.Analyzers/`.
   - Reads `DatabaseBannedSymbols.txt` and fails the build if server code attempts to execute raw SQL (`ExecuteSqlRaw`, `FromSqlRaw`), reference provider-specific assemblies directly, or use unportable LINQ constructs outside approved database abstractions.

---

### Cross-Engine Test Infrastructure

The testing harness in `tests/Jellyfin.Database.Testing/` enables fast, reliable cross-engine testing without code duplication:

- **`JellyfinDbTestFixture`:** Common xUnit test fixture managing database lifecycles.
- **Isolation Models:**
  - **SQLite & PostgreSQL:** Template database cloning. A clean migrated database serves as a template; new test databases are cloned in milliseconds via `CREATE DATABASE ... TEMPLATE`.
  - **MySQL, MariaDB & SQL Server (Step 22):** Leased database pool. Pre-provisions a fixed pool of databases. Between tests, `PurgeDatabase` clears tables, switches off foreign keys, and resets identity counters, avoiding expensive database creation.
- **`EngineTraits`:** Central capabilities and allowances registry for each engine (concurrency exceptions, identifier quoting, foreign key reset SQL, and model allowances).
- **`KnownGaps` Ledger:** Structured registry of known, accepted engine limitations with specific exception matchers (e.g. `SQLSERVER-INDEX-KEY-1700`). Tests expecting an engine quirk run and verify that the exact exception is raised; if the engine behavior changes or improves, the test fails.
- **64-Row Cross-Engine Conformance Kit (Step 22d, 27a):** A comprehensive suite running identical scenarios across all 5 engines:
  - Batch saves, identity generation, explicit identity inserts.
  - Write conflict retries, transaction isolation, deadlock resolution.
  - Null ordering, string collation, binary byte comparison.
  - Backup creation, purge, and full database restore.

---

## 2. Behaviour Changes on SQLite

While the primary goal was supporting additional engines, several necessary data cleanups and hardening improvements were made to SQLite itself. Every change was carefully designed to preserve backward compatibility:

| Step | Topic | Intentional SQLite Behaviour / Schema Change |
|---|---|---|
| **02 / 02b** | UTC Dates & Sentinels | All dates stored as normalized UTC strings. `DateTime.MinValue` and `MaxValue` converted to universal UTC sentinels (`0001-01-01T00:00:00Z` and `9999-12-31T23:59:59.9999999Z`), preventing timezone offset drift. |
| **03** | Date Sort Key & Image Timestamps | Year-only `PremiereDate` sort key calculation normalized to prevent locale/timezone reordering. Image comparison timestamp tolerance set to 1 second to accommodate filesystem timestamp resolution variations. |
| **04** | Comic Date Kind | Comic book metadata dates explicitly tagged with `DateTimeKind.Utc` upon parsing. |
| **05** | Case-Insensitive Matching | Replaced locale-dependent SQLite `COLLATE NOCASE` queries with `LikeIgnoreAsciiCase` and `CleanName`, ensuring consistent ASCII folding across all environments without ICU library dependency. |
| **06** | NUL Byte Stripping | Strips embedded `\0` bytes from paths and keys before saving. Migration `20261012120000_RemoveNulFromItemKeys` cleans historical NUL bytes from existing SQLite database rows. |
| **07** | Write Conflict Retry | Implemented `WriteConflictRetry` loop around EF Core save operations. Retries transient SQLite `SQLITE_BUSY` (database locked) errors with exponential backoff. |
| **08 / X10 / X14** | Backup & Restore Integrity | **08:** Full system restore verifies migration history and identity sequences.<br>**X10:** Added `DbSet<HomeSection>` so that custom home screen layouts are included in backup/restore.<br>**X14:** Backup archives exclude `database.xml`; restore preserves host `database.xml`, preventing SQLite settings from overwriting remote server credentials. |
| **19** | Indexed String Bounds | Maximum length constraints added to key-class indexed strings. Migration `20261013090000_NormalizeOverlongKeys` truncates historical overlong keys to fit index constraints. |
| **20** | Match Columns | Added persisted columns `BaseItems.CleanOriginalTitle` and `Peoples.NameLower` via migrations `20261013120000_AddNormalisedMatchColumns` and `20261013130000_BackfillNormalisedMatchColumns` for high-performance index seeks. |
| **20b** | Sort Name ASCII Fold | `NameStartsWith` queries use ASCII folding instead of full Unicode case folding. |
| **21** | Migration Guard | Creates `migration-in-progress.json` guard file during migrations to detect interrupted schema updates. |
| **X11** | Per-Item Batch Fallback | `SaveItemsSkippingFailures` retries failed multi-item batches individually when non-transient data errors occur, logging invalid rows rather than aborting library scans. |
| **X13** | Pessimistic Locking Refusal | Provider reports `SupportsPessimisticLocking = true` on SQLite (retaining historical single-writer lock), but server refuses `Pessimistic` on multi-connection server engines where thread-affine locks across `await` cause deadlocks. |

---

## 3. Test Evidence, CI Matrix & Real-World Production Migration Proof

### Test Suite Totals Across 5 Engines
Every code change was validated across all 5 database targets. Full suite runs (`s10`) pass with **zero failures** across all projects:

| Engine | Implementations.Tests | Server.Tests | Server.Integration.Tests | Controller.Tests | DatabaseAnalyzers.Tests | Status |
|---|---|---|---|---|---|---|
| **SQLite** | 2,865 Passed / 169 Skipped | 161 Passed / 11 Skipped | 137 Passed / 11 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |
| **PostgreSQL 16.15** | 2,863 Passed / 149 Skipped | 153 Passed / 18 Skipped | 144 Passed / 6 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |
| **MySQL 8.4.11** | 2,808 Passed / 202 Skipped | 131 Passed / 40 Skipped | 144 Passed / 6 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |
| **MariaDB 11.4.13** | 2,809 Passed / 201 Skipped | 131 Passed / 40 Skipped | 144 Passed / 6 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |
| **SQL Server 2022** | 2,913 Passed / 187 Skipped | 126 Passed / 45 Skipped | 144 Passed / 6 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |
| **SQL Server 2025** *(advisory)* | 2,913 Passed / 187 Skipped | 126 Passed / 45 Skipped | 144 Passed / 6 Skipped | 218 Passed / 0 Skipped | All Passed | **Pass (0 Fail)** |

*(Skipped tests represent expected provider-specific exclusions, such as SQLite-internal memory tests, or tests explicitly marked with an active `KnownGaps` entry.)*

### GitHub Actions CI Workflows
- **`ci-tests-db.yml`:** Reusable database testing workflow invoked by engine-specific matrices. Runs static analysis, code formatting, documentation checks, and targeted test projects.
- **`ci-tests-db-nightly.yml`:** Runs comprehensive test suites against the latest database releases.
- **`ci-tests-db-mysql.yml`:** Executes on every PR and nightly against `mysql:8.4.11` and `mariadb:11.4.13`, plus advisory legs on MySQL 9.7.2 and MariaDB 12.3.3.
- **`ci-tests-db-sqlserver.yml`:** Executes on every PR and nightly against SQL Server 2022 CU27-GDR1 (floor release 16.0) and advisory SQL Server 2025 CU9-GDR1 (17.0).

### Red Controls (Negative Testing)
Over 80 formal red controls were executed throughout the feature branches (e.g. 22/22 caught in Step 27, 8/8 in Step 28, 5/5 in Step 29, 14/14 in Step 27a). Each red control introduced a deliberate regression (removing registrations, breaking collation rules, altering migration IDs, or corrupting error codes) to verify that test gates turn red and revert cleanly.

### Real-World Production Migration Proof
Beyond unit and integration tests, an end-to-end migration of a real-world production database was executed:
- **Source Database:** Production Jellyfin SQLite database:
  - File size: **62 MB**
  - Schema: 31 tables, 53 migrations
  - Row counts: **12,169 `BaseItems`**, **7 `Users`**, **3,913 `UserData`**, **10,618 `Peoples`**, and hundreds of thousands of auxiliary metadata rows.
- **Process:**
  1. Full system backup created using `BackupService.CreateBackupAsync`.
  2. Backup restored into a clean **PostgreSQL 16.15** database using `CompleteDatabaseRestoreAsync`.
  3. Identity sequences re-synchronized and foreign key constraints fully verified.
- **Verification:**
  - Jellyfin server started against PostgreSQL.
  - Full client media streaming tested over NFS: verified direct play, transcoding, metadata retrieval, resume timestamps, and user progress updates without discrepancies.

---

## 4. Known Limitations, `KnownGaps` Ledger & Experimental Tiers (ADR-0011)

### Active `KnownGaps` Ledger
Differences between database engines that cannot be harmonized without unacceptable performance penalties or shared-model compromises are tracked in `KnownGaps.cs`:

| Gap Identifier | Engine | Description & Cause | Exception / Failure Matcher |
|---|---|---|---|
| **`SQLITE-CONTAINS-PARAMETER-LIMIT`** | SQLite | SQLite limits SQL variables (parameters) to 32,766. A plain LINQ `.Contains()` over >32k IDs throws an error. | `SqliteException` (code 1, "too many SQL variables") |
| **`MYSQL-SORTNAME-RANGE-CASE`** | MySQL | Range filters on `SortName` (`>= 'A' AND < 'B'`) fold non-ASCII accented characters differently under binary collation. | `EqualException` matching expected string list |
| **`MYSQL-NUL-EXACT-MATCH`** | MySQL | MySQL treats trailing NUL characters differently in binary string comparisons. | Exact match failure on trailing `\0` |
| **`SQLSERVER-SORTNAME-RANGE-CASE`** | SQL Server | SQL Server binary collations fold non-ASCII letters in range comparisons differently than Unicode rules. | `EqualException` matching expected string list |
| **`SQLSERVER-INDEX-KEY-1700`** | SQL Server | Nonclustered indexes in SQL Server have a maximum key size of 1700 bytes (ADR-0012). Long paths/titles exceeding this bound fail. | `SqlException` Msg 1946 |
| **`SQLSERVER-PATH-OVER-4000`** | SQL Server | Unbounded text-class strings are mapped to `nvarchar(4000)`. Paths longer than 4000 characters fail column validation. | `SqlException` Msg 2628 on `BaseItems.Path` |
| **`SQLSERVER-TRAILING-SPACE`** | SQL Server | Under ANSI standards, SQL Server pads strings with spaces before equality comparison. Two item values differing only by trailing spaces collide on unique indexes. | `SqlException` Msg 2601 on `IX_ItemValues_Type_Value` |
| **`SQLSERVER-UTF16-ORDER`** | SQL Server | SQL Server binary collations (`BIN2`) sort UTF-16 code units rather than Unicode code points. Characters outside the BMP (e.g. U+1F600) sort before U+E000. | `InvalidOperationException` matching UTF-16 order |

---

### Support Tiers & Promotion Criteria (ADR-0011)

Jellyfin defines two database tiers:
1. **Supported Tier (SQLite, PostgreSQL 16+):** Tier 1 support. Fully supported for all deployments, automated migration, and full CI gating.
2. **Experimental Tier (MySQL 8.4+, MariaDB 11.4+, SQL Server 2022+):** Community-supported. Operators must back up databases prior to upgrades. CI is non-blocking.

#### Promotion Criteria for MySQL & MariaDB (`ADR-0011 R1`)
- Zero transactional DDL workarounds: community validation that migration rollback handling via `migration-in-progress.json` is sufficient in field deployments.
- Confirmation that prefix index lengths on long text columns cause no functional lookup collisions in production libraries.
- Sustained CI stability across MySQL 8.4+ and MariaDB 11.4+ releases.

#### Promotion Criteria for SQL Server (`ADR-0011 R2`)
- Long-term validation of the 1700-byte index key budget and Step X11 per-item save fallback across large media collections (ADR-0012).
- Operator acceptance of `Latin1_General_100_BIN2` and `READ_COMMITTED_SNAPSHOT ON` requirements.
- Re-evaluation of UTF-8 column storage if SQL Server adds native Unicode code-point binary collations.

---

### Upstream-Notes List for Maintainers

1. **SQLite `PessimisticLockBehavior` Concurrency:**
   - In upstream Jellyfin, `PessimisticLockBehavior` uses a static `ReaderWriterLockSlim` held across `await` in asynchronous save calls (`OnSaveChangesAsync`).
   - A `ReaderWriterLockSlim` is thread-affine. When an `await` resumes on a different thread pool thread, calling `ExitWriteLock` throws `SynchronizationLockException`, permanently deadlocking subsequent writers.
   - Recommended fix for upstream: Replace `ReaderWriterLockSlim` with `SemaphoreSlim`, which is not thread-affine. Step X13 refused `Pessimistic` on multi-connection server providers, but SQLite still uses it if explicitly configured.
2. **Concurrency Tokens Restored as 0:**
   - Full database restores write entities using identity inserts and seed mechanisms, resulting in concurrency tokens being initialized to 0. This is safe because clients re-fetch fresh state after a restore.
3. **Plain `Contains` with Large ID Lists on SQLite:**
   - LINQ queries using `list.Contains(item.Id)` generate parameterized `IN (?, ?, ...)` clauses. On SQLite, exceeding 32,766 IDs triggers `SQLITE-CONTAINS-PARAMETER-LIMIT`. Shared query code should prefer batching or joining temporary tables when querying very large sets of IDs.
4. **`ImportDirectoryBuildProps=false` MSBuild Limitation:**
   - Several standalone test projects specify `ImportDirectoryBuildProps=false` to isolate build properties. Maintainers should be aware that centralized package versions must be explicitly mirrored if new dependencies are added to those test projects.

---

## 5. Suggested Commit Reading Order & Branch Inventory

### Suggested Reading Order by Group

Reviewers reading the commit series should follow this logical progression:

1. **Group 0: SQLite Hardening & Shared Conventions (Steps 01–08, 02b)**
   - *Theme:* Normalizing SQLite data types, UTC conversions, NUL stripping, and write conflict retries.
   - *Key Commits:* UTC date converters, `LikeIgnoreAsciiCase`, NUL cleanup migration, `WriteConflictRetry`.
2. **Group A: PostgreSQL Provider & Harness Foundations (Steps 10–18, X12)**
   - *Theme:* Establishing the provider model, testing harness, connection string overrides, and PostgreSQL support.
   - *Key Commits:* Test database fixtures, `PostgresDatabaseProvider`, PostgreSQL baseline migration, hash index for paths (X12).
3. **Group B: String Bounds & Match Columns (Steps 19–21, 20b)**
   - *Theme:* Model boundary validation and query performance.
   - *Key Commits:* Key length bounds, `CleanOriginalTitle` and `NameLower` columns, non-transactional migration guards.
4. **Group C0: Multi-Database Developer Workflow & Safety (Steps 22–23, 22b, 22c, 22d, 27a)**
   - *Theme:* Tooling to ensure multi-database maintenance is effortless and safe for all contributors.
   - *Key Commits:* Leased database pool, `dev/add-migration` and `PairedMigrationsIdGenerator`, `MigrationContentsTests`, Roslyn analyzers `JF0002`/`JF0003` enforcing symbol bans.
5. **Group C: MySQL & MariaDB Provider Family (Steps 24, 24b, 25, 26, 27a)**
   - *Theme:* Supporting MySQL 8.4+ and MariaDB 11.4+ via one provider.
   - *Key Commits:* `MySqlDatabaseProvider`, isolation level handling (`ReadCommittedConnection`), binary collation conventions, baseline migration, CI workflows.
6. **Group D: SQL Server Provider (Steps 27, 27b, 28, 29)**
   - *Theme:* Adding Microsoft SQL Server 2022+ support.
   - *Key Commits:* `SqlServerDatabaseProvider`, big-endian Guids, 1700-byte index budget handling, baseline migration, CI workflow, and operator documentation.
7. **Standalone Independent Fix Branches (`fix/*` & `X`-units):**
   - Independent bug fixes extracted during multi-db development:
     - `fix/migration-setup-premark` (X1): Pre-marking historical migrations on fresh installs.
     - `fix/probe-broadcast-date` (X2): Resilient parsing of tuner broadcast dates.
     - `fix/latest-episodes-null-date` (X3): Null checking `DateCreated` in latest episode queries.
     - `fix/activitylog-import-break` (X5): Preserving activity logs during database import.
     - `fix/backup-home-sections` (X10): Ensuring `HomeSection` is backed up and restored.
     - `fix/save-items-per-item-fallback` (X11): Per-item batch save fallback on non-transient data errors.
     - `multidb/x13-pessimistic-refusal` (X13): Refusing thread-affine pessimistic locks on server engines.
     - `fix/backup-keep-database-config` (X14): Excluding `database.xml` from backup archives.

---

### Complete Repository Branch Inventory (pr-plan S11)

In accordance with user direction (pr-plan S11), all intermediate step, standalone fix, and pre-review backup branches are cataloged below:

#### 1. Step Branches (`multidb/*`)
- `multidb/01-pin-ordering-and-grouping`
- `multidb/02-shared-utc-converter`
- `multidb/02b-utc-sentinels`
- `multidb/03-date-sort-key-and-tolerant-compare`
- `multidb/04-comic-date-kind`
- `multidb/05-case-insensitive-matching`
- `multidb/06-strip-nul`
- `multidb/07-write-conflict-retry`
- `multidb/08-restore-history-and-identity`
- `multidb/11-enrol-handrolled-db-tests`
- `multidb/12-connection-string-override`
- `multidb/14-postgres-conventions`
- `multidb/15-postgres-migrations-and-registration`
- `multidb/16-tests-on-migrated-schema`
- `multidb/17-ci-db-matrix`
- `multidb/18-integration-tests-on-postgres`
- `multidb/19-indexed-string-bounds`
- `multidb/19-work`, `19-work2`, `19-work3`, `19-work4`, `19-work5`
- `multidb/20-normalised-match-columns`
- `multidb/20-fold-b1`, `20-fold-work`
- `multidb/20b-sort-name-ascii-fold`
- `multidb/21-non-transactional-migration-guard`
- `multidb/22-harness-leased-pool`
- `multidb/22b-migration-workflow`
- `multidb/22c-merge-candidate`
- `multidb/22c-migration-contents-check`
- `multidb/22d-merge-candidate`
- `multidb/22d-raw-sql-ban-and-kit`
- `multidb/23-efcore-10.0.12`
- `multidb/24-mysql-provider`
- `multidb/24-dateplayed-fix`
- `multidb/25-mysql-migrations-and-registration`
- `multidb/25-before-rebase-2c70`, `25-before-sync-rebase`, `25-v2`
- `multidb/26-ci-mysql-mariadb`
- `multidb/27-sqlserver-provider`
- `multidb/27-sqlserver-provider-rework`, `27-sqlserver-provider-rework2`
- `multidb/27a-db-ban-analyzer`
- `multidb/27a-folderdelete-deterministic`
- `multidb/27a-kit-mysql`
- `multidb/27a-mysql-collation`
- `multidb/27a-mysql-isolation`
- `multidb/27b-sqlserver-encrypt`
- `multidb/28-sqlserver-migrations-and-registration`
- `multidb/29-ci-sqlserver`
- `multidb/ci-fix-1`, `ci-fix-2`
- `multidb/sync-upstream-20261015`
- `multidb/wip-infra`, `multidb/wip-infra-pre-reorder`
- `multidb/x10-merge-candidate`
- `multidb/x11-merge-candidate`, `multidb/x11-merge-candidate-pre-refold`
- `multidb/x12-pg-path-key`
- `multidb/x13-pessimistic-refusal`
- `multidb/x14-backup-database-config`
- `multidb/x3-on-fork`
- `multidb/x5-merge-candidate`, `x5-merge-candidate-old-tip`, `x5-merge-candidate-pre-ci`, `x5-merge-candidate-pre-text`, `x5-merge-candidate-text-before-sync`
- `multidb/x7-integration`, `x7-merge-candidate`
- `multidb/x8-integration`, `x8-merge-candidate`

#### 2. Standalone Bugfix Branches (`fix/*`)
- `fix/activitylog-import-break`, `fix/activitylog-import-break-old-tip`, `fix/activitylog-import-break-pre-text`, `fix/activitylog-import-break-text-before-sync`, `fix/activitylog-text-before`
- `fix/backup-home-sections`
- `fix/backup-keep-database-config`
- `fix/hls-extinf-invariant-culture`
- `fix/latest-episodes-null-date`
- `fix/migration-setup-premark`
- `fix/probe-broadcast-date`
- `fix/save-items-per-item-fallback`
- `fix/vtt-timestamp-map-header-only`, `fix/vtt-timestamp-map-header-only-v1`, `fix/vtt-timestamp-map-header-only-v2`

#### 3. Backup & Checkpoint Branches (`*-backup-*`)
- `backup/step21-before-sa1201-fix`
- `docs/27a-mysql-family-backup-a`
- `docs/x11-save-fallback-backup-a`
- `fix/backup-home-sections-backup-a`
- `fix/backup-keep-database-config-backup-a`
- `fix/latest-episodes-null-date-backup-a`
- `fix/save-items-per-item-fallback-backup-a`, `-backup-b`
- `multidb/02b-backup-a`
- `multidb/05-backup-c1`, `-backup-c2`, `-backup-c3`, `-backup-d`
- `multidb/06-backup-a` through `-backup-n` (14 checkpoints)
- `multidb/07-backup-a`
- `multidb/08-backup-a`, `-backup-b`, `-backup-c`
- `multidb/11-backup-a`
- `multidb/12-backup-a`, `-backup-b`, `-backup-c`
- `multidb/14-backup-a` through `-backup-g` (7 checkpoints)
- `multidb/15-backup-a` through `-backup-f` (6 checkpoints)
- `multidb/16-backup-a`
- `multidb/17-backup-a` through `-backup-e` (5 checkpoints)
- `multidb/18-backup-a`, `-backup-b`, `-backup-c`
- `multidb/19-backup-a` through `-backup-g`, `-backup-r2`, `-backup-r3` (9 checkpoints)
- `multidb/20-backup-gate1`
- `multidb/20b-backup-a`
- `multidb/21-backup-a` through `-backup-e` (5 checkpoints)
- `multidb/22-backup-a`
- `multidb/22b-backup-a` through `-backup-h` (8 checkpoints)
- `multidb/22c-merge-candidate-backup-a`, `-backup-b`, `-backup-c`
- `multidb/22c-migration-contents-check-backup-a`, `-backup-b`
- `multidb/22d-raw-sql-ban-and-kit-backup-a` through `-backup-d` (4 checkpoints)
- `multidb/24-backup-a` through `-backup-h`, `24-backup-gate1` (9 checkpoints)
- `multidb/24b-backup-a`, `-backup-b`
- `multidb/25-backup-a` through `-backup-g` (7 checkpoints)
- `multidb/26-ci-mysql-mariadb-backup-a` through `-backup-d` (4 checkpoints)
- `multidb/27-sqlserver-provider-backup-a` through `-backup-l` (12 checkpoints)
- `multidb/27a-db-ban-analyzer-backup-a` through `-backup-d` (4 checkpoints)
- `multidb/27a-kit-mysql-backup-a` through `-backup-d` (4 checkpoints)
- `multidb/27a-mysql-collation-backup-a`
- `multidb/27a-mysql-isolation-backup-a` through `-backup-c` (3 checkpoints)
- `multidb/27b-sqlserver-encrypt-backup-a`
- `multidb/28-sqlserver-migrations-and-registration-backup-a`
- `multidb/ci-fix-1-backup-a`
- `multidb/wip-infra-backup-r3`, `-backup-r4`, `-backup-r5`
- `multidb/x10-merge-candidate-backup-a`
- `multidb/x11-merge-candidate-backup-a`
- `multidb/x12-pg-path-key-backup-a`, `-backup-b`
- `multidb/x13-pessimistic-refusal-backup-a`

#### 4. Primary Integration & Upstream Replay Branches
- `master`: Upstream tracking branch.
- `feature/multi-db`: Active integration branch containing all merged steps, explainers, and ADRs.
- `multidb/upstream`: Clean linear replay branch on `upstream/master` (`c7ce6a1b56`) containing the Supported tier (SQLite + PostgreSQL), full test harness, conformance kit, migration pairing tools, and symbol ban analyzers, with zero merge commits and no `docs/adr/` files.
- `multidb/upstream-all`: Clean linear replay branch on `upstream/master` (`c7ce6a1b56`) containing all 5 database providers (SQLite, PostgreSQL, MySQL, MariaDB, SQL Server) replayed linearly with zero merge commits and no `docs/adr/` files.
