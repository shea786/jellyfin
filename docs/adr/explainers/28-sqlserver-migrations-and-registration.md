# Explainer: step 28, the SQL Server migrations and registration

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/28-sqlserver-migrations-and-registration`, 7 code commits on `feature/multi-db` tip `9553f4faf5`, plus the documentation commit.
  - `bf45ae7c05` Add the SQL Server baseline migration
  - `de90fa25ff` Test the migrated SQL Server schema on a server
  - `7fcc558a3d` Register the SQL Server provider
  - `78446af117` Start the integration server on SQL Server
  - `2d6a0e66df` Give the SQL Server provider migrations the id of the SQLite migration
  - `214248bc2b` Generate the SQL Server migration with dev/add-migration
  - `add5c340ea` Check the SQL Server migrations with the workflow tests
- **Size:** 24 files, +5885/-55. About 5,100 of those lines are the generated baseline, designer file, and model snapshot. Production code and commands are about 70 lines; the rest is tests.
- **Status:** reviewed over two rounds (backup `multidb/28-sqlserver-migrations-and-registration-backup-a`, `d4fd137db6`). Red controls 8/8 caught and reverted clean (`/tmp/28/red/results.json`, `COMPLETE`). Per-commit gates 134/134 rows rc 0 across 105 test legs, 14,895 TRX rows with 0 failures (`/tmp/28/gates/g1/results.tsv`, `COMPLETE`).
- **Relevant decisions:** ADR-0001 W1–W3, ADR-0011 tiers (Experimental, once-per-database warning), ADR-0012 Test 5 (the 18 over-budget indexes), pr-plan 28 and B41.
- **With this step a server can run on SQL Server.** A `database.xml` that names `Jellyfin-SqlServer` selects the provider. A fresh database migrates from an empty state in one baseline, and the integration suite starts the server on SQL Server.

## Why it exists

Step 27 built the SQL Server provider project and conventions, but did not register it in the dependency injection container and added no migrations. Tests ran against a schema built from the model, and migration-dependent tests were skipped. This step adds the baseline migration and registration, starts the integration server against SQL Server, and enrolls SQL Server in `dev/add-migration` and the migration workflow checks (ADR-0001 W1–W3).

## What each commit does

- **The baseline migration (`bf45ae7c05`).** Adds `20200101000000_InitialCreate`, generated from the model through a design-time factory without connecting to a server.
  - **Coverage:** `SqlServerMigrationBaseline.CoversThroughSqliteMigration` is `20261016043615_IndexItemPathByHash` (X12). Like PostgreSQL and MySQL, earlier SQLite migrations are covered by the baseline and never repeated for SQL Server.
  - **Design-time factory:** `SqlServerDesignTimeJellyfinDbFactory` builds options with `ServerVersion 16.0` (SQL Server 2022) and dummy connection strings, so scaffolding produces identical output regardless of machine.
  - **Traits and offline tests:** `EngineTraits.HasSchemaMigrations` is set to `true` for SQL Server. Offline tests verify the baseline sorts before any SQLite migration, the covered SQLite migration exists, and the design-time factory builds the model and uses the SQL Server assembly migrations.
- **The migrated schema on a real server (`de90fa25ff`).** On a live SQL Server instance, tests migrate an empty database and verify:
  - every migration is applied starting with the baseline, and none is pending;
  - the model has no pending changes;
  - tables, columns, indexes, and foreign keys match a database built from the model (read from `sys` views);
  - text columns have `Latin1_General_100_BIN2` collation, indexed text-class columns are `nvarchar(4000)`, and unindexed text columns outside keys are `nvarchar(max)`;
  - **ADR-0012 Test 5:** `MigratedDatabase_HoldsTheEighteenOverBudgetIndexes` migrates an empty database with a `ListLogger`, asserting that all 18 composite indexes exceeding 1700 bytes exist in `sys.indexes` and that zero `Error` or `Critical` entries are logged during migration (the expected SQL Server Msg 1945 warnings do not fail migration);
  - **Transactional DDL rollback:** `SqlServerMigrationAtomicityTests` verifies that because SQL Server supports transactional DDL, a failing migration rolls back completely, leaving no partial tables and no history row.
- **Registration and the experimental warning (`7fcc558a3d`).**
  - `Jellyfin-SqlServer` is added to `DatabaseProviderTypes` in `ServiceCollectionExtensions`, and `Jellyfin.Server.Implementations` references `Jellyfin.Database.Providers.SqlServer`.
  - `EngineTraits.IsRegistered` is set to `true` for SQL Server.
  - When selected, the provider logs a once-per-database Warning that it is experimental.
  - Provider registration and locking behavior tests run for SQL Server via the registered provider path.
- **Integration server startup and start-over tests (`78446af117`).**
  - `JellyfinApplicationFactory` starts on SQL Server as well as PostgreSQL, MySQL, and MariaDB.
  - `JellyfinApplicationFactoryTests` and `StartOverTests` run against SQL Server. Disposing the factory verifies the database is dropped by attempting an unpooled connection and catching error 4060 (`Cannot open database`).
  - `StartOverTests` clears SQL Server connection pools and drops all user tables and foreign keys via `sys` views when simulating an emptied database.
- **Paired migration ids (`2d6a0e66df`).**
  - Registers `SqlServerDesignTimeServices` with `[DesignTimeServicesReference]`, sharing the internal `PairedMigrationsIdGenerator` from `Jellyfin.Database.Implementations.DesignTime` (introduced in 22c).
  - New SQL Server migrations automatically reuse the timestamp and id of the corresponding SQLite migration.
  - `PairedMigrationsDesignTimeServicesTests` verifies resolving the SQLite migrations directory from the SQL Server provider output path.
- **`dev/add-migration` generates the SQL Server migration (`214248bc2b`).**
  - Adds the SQL Server table row to `dev/add-migration`. One command scaffolds changes across SQLite, PostgreSQL, MySQL, and SQL Server simultaneously.
  - `src/Jellyfin.Database/readme.md` lists SQL Server alongside the other providers with its baseline migration and minimum supported server version (SQL Server 2022).
- **Workflow tests and allowlist update (`add5c340ea`).**
  - `MigrationProvider.All` enrolls `SQL Server` in the workflow tests, checking for pending model changes, paired migration IDs, and migration contents comparisons.
  - `MigrationContentsAllowlist.json` is updated:
    - `Peoples (NameLower)` index, `BaseItems.CleanOriginalTitle` column, `Peoples.NameLower` column, and the `PLACEHOLDER` seed row update provider from `"MySQL 8.4"` to `"MySQL 8.4, SQL Server"` (`BaselineCoverage`).
    - 4 entries are added for foreign keys on delete `NoAction` vs `Cascade` (`EngineSpecific`): `AncestorIds.ParentItemId` and `BaseItems.ParentId` use `NoAction` on SQL Server to avoid Msg 1785 (multiple cascade paths and cyclic cascade paths), while PostgreSQL and MySQL use `Cascade`.

## What the review found and what was done

Two review rounds were conducted against backup `multidb/28-sqlserver-migrations-and-registration-backup-a` (`d4fd137db6`):

1. **Commit 3 per-commit gate independence:** In `JellyfinApplicationFactoryTests.cs`, the switch on `ProviderTypeOfTheRun` needed `TestDbProvider.SqlServer => typeof(SqlServerDatabaseProvider)`. In the original branch, this mapping was deferred to Commit 4 (`78446af117`). Running the per-commit test gates on SQL Server at Commit 3 (`7fcc558a3d`), where SQL Server became registered, threw `NotSupportedException`. Moving the switch arm into Commit 3 ensured that Commit 3 passes all gates independently.
2. **SqlServerBaselineSchemaTests unused context:** In `MigratedDatabase_TextColumns_HaveBinaryCollationAndExpectedTypes`, an unused `context` variable created before the raw ADO query was removed.
3. **ADR-0012 Test 5 ListLogger assertion:** `MigratedDatabase_HoldsTheEighteenOverBudgetIndexes` was enhanced from asserting pre-migrated index names to creating an empty database, running `MigrateAsync` with a custom `ListLogger`, and explicitly asserting that `logger.Entries` contains zero `LogLevel.Error` or `LogLevel.Critical` records while verifying all 18 over-budget indexes exist.

## Red controls and per-commit gates

- **Red controls (`/tmp/28/red/results.json`, `COMPLETE`):** 8/8 controls caught and reverted clean:
  1. `remove-registration`: removing SQL Server from `ServiceCollectionExtensions` failed registration tests.
  2. `break-covers-through-sqlite`: altering covered SQLite id failed baseline and pairing tests.
  3. `remove-designtime-services-attribute`: removing the design-time attribute failed paired id generator resolution tests.
  4. `break-allowlist-entry`: modifying an allowlist entry failed `MigrationContentsTests`.
  5. `remove-designtime-factory`: removing the design-time factory failed `SqlServerDesignTimeFactoryTests`.
  6. `traits-not-registered`: flipping `EngineTraits.IsRegistered` failed `EngineTraitsTests`.
  7. `break-pairing-generator`: breaking the generator binding failed paired id generator tests.
  8. `overbudget-count-check`: tampering with the over-budget index count failed `MigratedDatabase_HoldsTheEighteenOverBudgetIndexes`.
- **Per-commit gates (`/tmp/28/gates/g1/results.tsv`, `COMPLETE`):**
  - 134/134 gate rows exited 0 across the base (`9553f4faf5`) and all 7 code commits.
  - Debug builds: 0 errors, 211 warnings (matching base warning set exactly).
  - Release builds: 0 warnings, 0 errors.
  - Code formatting (`dotnet format`): 0 changes needed.
  - Render check (`dotnet run docs/database/render -- --check`): up to date.
  - 105 test legs across SQLite, PostgreSQL, MySQL, MariaDB, and SQL Server.
  - 14,895 TRX test rows: 13,418 Passed, 1,477 Skipped (expected provider-specific skips), 0 Failed.

## Limits

- SQL Server remains an Experimental, fork-only provider (ADR-0011).
- The 18 composite indexes exceeding 1700 bytes emit warning Msg 1945 during baseline creation, which is accepted under ADR-0012.
- Text-class columns longer than 850 characters are refused at the database level when indexed (ADR-0009/G2, ADR-0012).

## Things worth knowing when defending it

- **Why does the baseline cover through `IndexItemPathByHash`?** That was the newest SQLite migration at the time (X12). Like PostgreSQL and MySQL baselines, a fresh provider's baseline starts at the latest migration of the reference provider, so earlier migrations need not be repeated.
- **Why `NoAction` on `ParentId` and `ParentItemId` foreign keys?** SQL Server raises Msg 1785 if multiple cascade paths exist to `AncestorIds` or if a self-referencing relationship cascades on delete. `NoAction` prevents this error, and Jellyfin's application-level cleanup handles cascading deletions.
- **Why transactional DDL tests?** Unlike MySQL/MariaDB, SQL Server natively supports transactional DDL. If a migration fails midway, SQL Server rolls back all changes in the migration transaction, leaving the database clean and with no migration marker needed.
