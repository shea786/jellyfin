# Upstream PR plan (stacked)

This file stays in the fork and never goes upstream. The user writes all PR titles and bodies, because Jellyfin forbids LLM-written direct communication. Nothing here is PR text.

## Rules

- `multidb/01-…` branches from `upstream/master`. Every later branch branches from the one before it.
- Each PR builds warning-free (`TreatWarningsAsErrors`), passes the existing matrix, and leaves SQLite fully working.
- Portability PRs (01-08) contain only provider-agnostic changes, plus SQLite tests that pin the behaviour and follow the rules in ADR-0002. They must not add Postgres references.
- Scopes 01-05 and 08 follow ADRs 0004-0009 (DB parity, still pending). When the parity task list lands, refine their file lists. Do not renumber.

**Local TDD.**

- The fork-only integration branch `feature/multi-db` carries the whole stack, the `docs/adr/` commits and work in progress. Infrastructure and the Postgres provider are built there first, so portability tests can go red and then green on Postgres before their PR is carved out.
- On `feature/multi-db`, portability tests derive from `JellyfinDbTestFixture`. In the carved PRs 01-08 they derive from `SqliteDbTestFixture`, a one-token difference. PR 10 performs the rename.

## Stack

| # | Branch | Scope | Key files | Tests added | Depends on |
|---|--------|-------|-----------|-------------|------------|
| 01 | `multidb/01-guid-min-aggregate` | Rewrite `Min()` over Guid keys as provider-neutral LINQ (ADR-0007) | `Jellyfin.Server.Implementations/Item/BaseItemRepository.QueryBuilding.cs:103,109`, `BaseItemRepository.ByName.cs:259` | Item tests for alternate-version / by-name grouping that check deterministic representative selection | upstream/master |
| 02 | `multidb/02-shared-utc-converter` | Move the DateTimeKind/UTC convention from the SQLite provider into Implementations and apply it to all providers (ADR-0004) | `Providers.Sqlite/ValueConverters/DateTimeKindValueConverter.cs`, `Providers.Sqlite/ModelBuilderExtensions.cs`, `SqliteDatabaseProvider.cs:142-145`, `JellyfinDbContext.cs:326-339` | Round-trip of Local/Unspecified/Utc values; `HasPendingModelChanges` is still false on SQLite | 01 |
| 03 | `multidb/03-case-insensitive-search` | Explicit case-insensitive matching instead of relying on SQLite `LIKE`/NOCASE (ADR-0005) | `Activity/ActivityManager.cs:82-108`, `Item/BaseItemRepository.TranslateQuery.cs:268-273,489-496,1075-1090`, `Emby.Server.Implementations/Library/Search/SqlSearchProvider.cs:127-134` | Mixed-case search, name filters and provider-ID filters | 02 |
| 04 | `multidb/04-explicit-null-ordering` | Put NULL ordering in the query, where it currently depends on the engine (ADR-0006) | `Item/OrderMapper.cs`, the BaseItemRepository ordering paths | Ordering with NULLs, ascending and descending | 03 |
| 05 | `multidb/05-maxlength-truncation` | Enforce or truncate declared `MaxLength` before saving (ADR-0009) | Entity configs in `Jellyfin.Database.Implementations/ModelConfiguration/*`, the write paths found by the parity inventory | Saving over-length values | 04 |
| 06 | `multidb/06-restore-history-and-sequences` | Backup restore: keep the archive's code-routine history rows and the target provider's EF rows; add an additive provider member to reseed identities, default no-op (ADR-0001/0003) | `FullSystemBackup/BackupService.cs:160-215`, `IJellyfinDatabaseProvider.cs` (new default member), `SqliteDatabaseProvider.cs` | Restore round-trip on SQLite; history contents after restore | 05 |
| 07 | `multidb/07-provider-aware-routines` | Replace `sqlite_sequence` and `json_*` SQL in code routines with LINQ or provider members (ADR-0001) | `Migrations/Routines/20250420070000_MigrateActivityLogDb.cs:87`, `20260911120000_StripEmbeddedLinkedChildren.cs` | Routine tests in `tests/Jellyfin.Server.Tests/Migrations/` | 06 |
| 08 | `multidb/08-concurrency-tokens` | *Only if ADR-0008 requires a code change:* portable concurrency tokens and locking | Per ADR-0008 | Per ADR-0008 | 07 |
| 09 | `multidb/09-fix-setup-migration-seeding` | Fix `!appliedMigrations.Any(f => f != id)` so it checks membership | `Jellyfin.Server/Migrations/JellyfinMigrationService.cs:121` | `JellyfinMigrationServiceTests`: setup with non-empty history pre-marks only the missing routines | 08 |
| 10 | `multidb/10-db-test-infrastructure` | `tests/Jellyfin.Database.Testing` with `DatabaseTestFixture`, SQLite backend, ambient selection, `KnownGap` ledger; rename `SqliteDbTestFixture` to `JellyfinDbTestFixture` (ADR-0002) | New project, `Jellyfin.sln`, `Directory.Packages.props` (`xunit.v3.extensibility.core`, `xunit.v3.assert` 3.2.2), 33 one-line base-class changes | Infrastructure self-tests; migration tests (a) for SQLite | 09 |
| 11 | `multidb/11-enrol-handrolled-db-tests` | Move the ~11 hand-rolled `SqliteConnection` classes onto the fixture; ban direct SQLite use in tests | `BackupServiceTests.cs`, `Users/*`, `Security/AuthenticationManagerTests.cs`, `Library/UserDataManagerTests.cs`, `Item/ItemCountServiceTests.cs`, `Jellyfin.Server.Tests/Migrations/*`, `tests/BannedSymbols.Tests.txt`, `tests/Directory.Build.props` | Existing tests, now enrolled | 10 |
| 12 | `multidb/12-connection-string-override` | `database:ConnectionString` override, applied after the write-back and never persisted (ADR-0001) | `Jellyfin.Server.Implementations/Extensions/ServiceCollectionExtensions.cs:79-104` | Override applies, is not saved, and works for plugin providers | 11 |
| 13 | `multidb/13-postgres-provider` | `Jellyfin.Database.Providers.Postgres` with the provider class and its members (ADR-0003), Postgres test backend, `[PostgresFact]`; **not yet registered**, so users cannot select it | New project, `Jellyfin.sln`, `Directory.Packages.props` (`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Testcontainers.PostgreSql` 4.16.0) | Provider member tests (purge, optimise, sentinel backup); the suite under `JELLYFIN_TEST_DB_PROVIDER=Postgres` with gaps recorded | 12 |
| 14 | `multidb/14-postgres-migrations-and-registration` | `20200101000000_InitialCreate`, snapshot, design-time factory, `PostgresMigrationBaseline`; add the provider to `DatabaseProviderTypes()` | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c) for both providers, plus the first-run flow on Postgres | 13 |
| 15 | `multidb/15-tests-on-migrated-schema` | Default `TestSchemaSource` becomes `Migrations` (template per process) | `tests/Jellyfin.Database.Testing/*` | Existing tests | 14 |
| 16 | `multidb/16-ci-postgres-job` | `run-tests-postgres` job with sentinel guard (ADR-0010) | `.github/workflows/ci-tests.yml`, `tests/postgres.runsettings` | Sentinel test | 15 |
| 17 | `multidb/17-integration-tests-on-postgres` | `JellyfinApplicationFactory` writes a Postgres `database.xml` when Postgres is selected | `tests/Jellyfin.Server.Integration.Tests/JellyfinApplicationFactory.cs` | Integration suite on Postgres | 16 |
