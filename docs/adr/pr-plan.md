# Upstream PR plan (stacked)

This file stays in the fork and never goes upstream. The user writes all PR titles and bodies, because Jellyfin forbids LLM-written direct communication. Nothing here is PR text.

## Rules

- **Branching.**
  - Within a group, every branch is cut from the one before it.
  - Group 0 starts from `upstream/master`.
  - Groups C and D both start from the tip of C0 and do not depend on each other, so their order can swap (ADR-0011).
  - Rows marked *independent* start from `upstream/master` and can be carved out at any time.
- **Every PR:**
  - builds warning-free (`TreatWarningsAsErrors`);
  - passes the existing matrix;
  - leaves SQLite fully working.
- **Portability PRs (group 0 and group C0):**
  - contain only provider-neutral changes, plus SQLite tests that pin the behaviour and follow the rules in ADR-0002;
  - add no references to any server engine.
- **SQLite migrations.** The **SQLite migration** column marks the only PR that carries one. Every other PR is SQLite-neutral: no migration file, and `HasPendingModelChanges` stays false.

**Local TDD.**

- The fork-only integration branch `feature/multi-db` carries the whole stack, the `docs/adr/` commits and work in progress.
- The test infrastructure and the providers are built there first, so a portability test can go red and then green on every engine before its PR is carved out.
- On `feature/multi-db`, portability tests derive from `JellyfinDbTestFixture`. In the carved group 0 PRs they derive from `SqliteDbTestFixture`, a one-token difference. PR 10 performs the rename.

**Numbering changed on 2026-10-12** (old number → new):

| Old | New |
|---|---|
| 01 Guid `MIN` rewrite | dropped (ADR-0007); now 01, which only pins the behaviour |
| 02 | 02 |
| 03 | 05 |
| 04 NULL ordering | moved into the Postgres provider (14), per ADR-0006 R1 |
| 05 `MaxLength` truncation | dropped (ADR-0009); NUL stripping is now 06 |
| 06 | 08 |
| 07 | 09 |
| 08 | 07 |
| 09 | X1 |
| 10-13 | unchanged |
| 14-17 | 15-18 |

## Group 0: Portability

These PRs reconcile with the parity task list P1-P7. P8 is a Postgres item and moved to group B.

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 01 | `multidb/01-pin-ordering-and-grouping` | P1. Tests only: pin the Guid representative selection, string code-point order and NULL order (ADR-0006 (a), ADR-0007 (a)). No production change. | `tests/Jellyfin.Server.Implementations.Tests/Item/*` | ADR-0007 a1-a4, ADR-0006 a1-a4 | upstream/master | no |
| 02 | `multidb/02-shared-utc-converter` | P2. Shared `UtcDateTimeConverter` with no truncation (ADR-0004 R1); remove `SetDefaultDateTimeKind` from SQLite | `Providers.Sqlite/ValueConverters/DateTimeKindValueConverter.cs`, `Providers.Sqlite/ModelBuilderExtensions.cs`, `SqliteDatabaseProvider.cs:142-145`, `JellyfinDbContext.cs:336-340` | ADR-0004 a1 (R2), a2; `HasPendingModelChanges` false | 01 | no |
| 03 | `multidb/03-date-sort-key-and-tolerant-compare` | P3. Year-only `PremiereDate` sort key; tolerant timestamp comparison for images | `Item/OrderMapper.cs:69`, `BaseDynamicImageProvider.cs:299`, `DateTimeExtensions.EqualsToMicrosecond` | ADR-0004 a3, a4, a5 (`HasChangedByDate`) | 02 | no |
| 04 | `multidb/04-comic-date-kind` | P4. Comic calendar dates use `DateTimeKind.Utc` | `ComicInfoReader.cs:214`, `ComicBookInfoProvider.cs:238` | Parsers return `Kind == Utc` with the calendar date kept | 03 | no |
| 05 | `multidb/05-case-insensitive-matching` | P5. `LikePatterns`, option D, the `NameContains`→`CleanName` fix, ban on the two-argument `Like` (ADR-0005) | `Jellyfin.Database.Implementations/LikePatterns.cs`, `Activity/ActivityManager.cs:80-108`, `Item/BaseItemRepository.TranslateQuery.cs:268,273,489,496`, `Library/Search/SqlSearchProvider.cs:125-134`, `Item/PeopleRepository.cs:482`, `BannedSymbols.txt` | ADR-0005 a1-a7; benchmark R1 | 04 | no |
| 06 | `multidb/06-strip-nul` | P6. `SanitizeStrings()` in `SaveChanges`; strip NUL from query input (ADR-0009) | `JellyfinDbContext.cs:265,291`, `LikePatterns`, `StringExtensions.GetCleanValue` | ADR-0009 a1-a4 | 05 | no |
| 07 | `multidb/07-write-conflict-retry` | P7. `IsRetryableWriteConflict` (default `false`), SQLite classifier, `WriteConflictRetry` (ADR-0008) | `IJellyfinDatabaseProvider.cs`, `SqliteDatabaseProvider.cs`, `Item/ItemPersistenceService.cs:334-388`, `ReconcileUserDataAsync` | ADR-0008 a1-a3 | 06 | no |
| 08 | `multidb/08-restore-history-and-identity` | Restore keeps the archive's code-routine rows and the target provider's EF rows. Pre-flight runs before the purge. Each table is saved inside an identity scope. Adds `BeginExplicitIdentityInsertAsync` and `ResetIdentitySequencesAsync` (no-op defaults). See ADR-0003 S2. | `FullSystemBackup/BackupService.cs:160-250`, `IJellyfinDatabaseProvider.cs` | SQLite restore round trip; history contents; a failed pre-flight leaves the database untouched; an insert after restore gets an ID above the maximum | 07 | no |
| 09 | `multidb/09-provider-neutral-routines` | Replace `sqlite_sequence` and `json_*` SQL in the code routines (ADR-0001) | `Routines/20250420070000_MigrateActivityLogDb.cs:87`, `Routines/20260911120000_StripEmbeddedLinkedChildren.cs:31` | Routine tests in `tests/Jellyfin.Server.Tests/Migrations/` | 08 | no |
| X1 | `fix/migration-setup-premark` (exists; outside the stack) | Fix `!appliedMigrations.Any(f => f != id)` so that it checks membership | `Jellyfin.Server/Migrations/JellyfinMigrationService.cs:121` | `JellyfinMigrationServiceTests`: setup with non-empty history pre-marks only the missing routines | upstream/master (*independent*) | no |

## Group A: Test harness (SQLite backend only)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 10 | `multidb/10-db-test-infrastructure` | `tests/Jellyfin.Database.Testing` with the **generic** backend contract (ADR-0002 H1, H3), the SQLite backend, ambient selection and the `KnownGap` ledger. The rename to `JellyfinDbTestFixture` is its own commit (A2). | New project, `Jellyfin.sln`, `Directory.Packages.props`, 33 one-line base-class changes | Infrastructure self-tests; migration test (a) for SQLite | 09 | no |
| 11 | `multidb/11-enrol-handrolled-db-tests` | Move the ~11 hand-rolled `SqliteConnection` classes onto the fixture; ban direct SQLite use in tests | `BackupServiceTests.cs`, `Users/*`, `Jellyfin.Server.Tests/Migrations/*`, `tests/BannedSymbols.Tests.txt` | The existing tests, now enrolled | 10 | no |

## Group B: PostgreSQL (Supported)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 12 | `multidb/12-connection-string-override` | `database:ConnectionString` override, applied after the write-back and never persisted (ADR-0001) | `Extensions/ServiceCollectionExtensions.cs:79-104` | The override applies, is not saved, and works for plugin providers | 11 | no |
| 13 | `multidb/13-postgres-provider` | The provider class and its members (ADR-0003, including A1 Throw, `setval` reset and the purge), the Postgres test backend (template clone) and `ProviderFact`. **Not registered yet.** | New `Jellyfin.Database.Providers.Postgres`, `Directory.Packages.props` (Npgsql EF 10.0.3, `Testcontainers.PostgreSql` 4.16.0) | Member tests; the suite under `Postgres`, with gaps recorded | 12 | no |
| 14 | `multidb/14-postgres-conventions` | P8. `"C"` collation and `text` (ADR-0006, ADR-0009), the `NULLS FIRST`/`LAST` generator and index convention (ADR-0006 R2), `Timezone=UTC` (ADR-0004), and the shared `IdentifierLengthConvention` (ADR-0001 M4) | Provider `Conventions/`, `Jellyfin.Database.Implementations/Conventions/IdentifierLengthConvention.cs` | The `[ProviderFact]` checks in ADRs 0004, 0006 and 0009; model conformance (identifiers) | 13 | no |
| 15 | `multidb/15-postgres-migrations-and-registration` | Baseline `20200101000000_InitialCreate`, including `lower("Name")` and `jf_uuid_smaller`/`min(uuid)` (ADR-0007 R1/R2), `PostgresMigrationBaseline`; register the provider | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c), the failing-migration test (A1), the ADR-0007 aggregate tests, first run | 14 | no |
| 16 | `multidb/16-tests-on-migrated-schema` | The default `TestSchemaSource` becomes `Migrations` | `tests/Jellyfin.Database.Testing/*` | The existing tests | 15 | no |
| 17 | `multidb/17-ci-db-matrix` | The `run-tests-db` matrix with only the Postgres entry, and the generic sentinel (ADR-0010 CI1-CI2) | `.github/workflows/ci-tests.yml`, `tests/postgres.runsettings` | `DatabaseBackend_IsLive` | 16 | no |
| 18 | `multidb/18-integration-tests-on-postgres` | `JellyfinApplicationFactory` writes the selected provider's `database.xml` | `tests/Jellyfin.Server.Integration.Tests/JellyfinApplicationFactory.cs` | The integration suite on Postgres | 17 | no |

## Group C0: Shared prerequisites for further engines (provider-neutral)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 19 | `multidb/19-indexed-string-bounds` | Key-class `MaxLength`, `EnforceKeyLengths()`, ingest truncation of `Role`/`Value`, and the text-class annotation (ADR-0009 L1-L2). Snapshots are refreshed with no migration file (verified: zero SQLite operations). | Entities and `ModelConfiguration/*` for the 9 key-class columns, `JellyfinDbContext.cs`, `Item/PeopleRepository.cs`, `Item/ItemPersistenceService.cs`, both snapshots | ADR-0009 L5; model conformance (key budgets per engine); `HasPendingModelChanges` false on SQLite and PG | 18 | no (verified) |
| 20 | `multidb/20-normalised-match-columns` | Option E: `BaseItems.CleanOriginalTitle`, `Peoples.NameLower`, backfill routine; queries switch to these columns (ADR-0005 E1-E4) | Entities, `BaseItemMapper`, `SqlSearchProvider.cs`, `TranslateQuery.cs:268,273`, `PeopleRepository.cs`, new SQLite and PG migrations with the same ID, a new code routine | Tests 2 and 7 flipped; backfill routine test; E4 behaviours | 19 | **yes**: 2 × AddColumn, index swap, batched backfill |
| 21 | `multidb/21-non-transactional-migration-guard` | `SupportsTransactionalMigrations` (default `true`) and the `migration-in-progress.json` marker (ADR-0003 S1) | `IJellyfinDatabaseProvider.cs`, `Jellyfin.Server/Migrations/JellyfinMigrationService.cs:546` | Marker lifecycle and startup refusal, using a test provider that returns `false` | 20 | no |
| 22 | `multidb/22-harness-leased-pool` | The generic leased-pool strategy: purge plus seed data, and `fresh: true` (ADR-0002 H1-H2) | `tests/Jellyfin.Database.Testing/*` | Pool self-tests on SQLite file databases | 21 | no |

## Group C: MySQL/MariaDB (Experimental, fork-only until ADR-0011 R1 is met; one provider, two matrix entries)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 23 | `multidb/23-efcore-10.0.12` | Bump every EF Core package and `HealthChecks.EntityFrameworkCore` to 10.0.12. Npgsql 10.0.3 still fits. | `Directory.Packages.props:36-45` | The existing suites | upstream/master (*independent*; carve early) | no |
| 24 | `multidb/24-mysql-provider` | `Jellyfin.Database.Providers.MySql` (Microting 10.0.12) and its members. Conventions: collation by server type (ADR-0006 C2), `binary(16)` Guid (ADR-0007 G2), `datetime(6)` with the `MaxValue` clamp (ADR-0004 T3), `longtext` with prefix lengths (ADR-0009 L3), all filters removed (ADR-0001 M3), identifier limit 64, interceptor for strict mode and READ COMMITTED (ADR-0008 R3), retry codes, purge, `SupportsTransactionalMigrations = false`. Backends: `MySql`, `MariaDb` (pool). **Not registered.** | New project, `Directory.Packages.props` (`Microting.EntityFrameworkCore.MySql`, `Testcontainers.MySql`/`MariaDb` 4.16.0) | Member and classifier tests; the suite on both servers, with gaps recorded; model conformance | 22, 23 | no |
| 25 | `multidb/25-mysql-migrations-and-registration` | Baseline `20200101000000_InitialCreate` covering through PR 20's migration; register `Jellyfin-MySQL` with the Experimental warning | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c) on both servers; safety-net test (S1); explicit `SHOW INDEX` prefixes | 24 | no |
| 26 | `multidb/26-ci-mysql-mariadb` | Add `MySql` and `MariaDb` matrix entries, not required | `.github/workflows/ci-tests.yml` | The sentinel on both | 25 | no |

## Group D: SQL Server (Experimental, fork-only unless maintainers ask; ADR-0011 R2)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 27 | `multidb/27-sqlserver-provider` | `Jellyfin.Database.Providers.SqlServer` and its members (identity-insert scope, purge with `DELETE` + `RESEED`, retry codes, RCSI warning). Conventions: `binary(16)` Guid converter, BIN2 collation, `nvarchar(4000)` for text-class columns, OR filter dropped, `NoAction` on the 2 cascade FKs (ADR-0001 M5). Pool backend. **Not registered.** | New project, `Directory.Packages.props` (`Microsoft.EntityFrameworkCore.SqlServer` at the EF version, `Testcontainers.MsSql` 4.16.0) | Member tests; the suite with ADR-0006 G1/G2 and ADR-0009 G2 recorded; folder delete (M5); unique-NULL filter (M6) | 22 | no |
| 28 | `multidb/28-sqlserver-migrations-and-registration` | Baseline and registration of `Jellyfin-SqlServer` with the Experimental warning | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c); failing-migration test | 27 | no |
| 29 | `multidb/29-ci-sqlserver` | Add the `SqlServer` matrix entry, not required (ADR-0010 CI3) | `.github/workflows/ci-tests.yml` | Sentinel | 28 | no |

## CI per engine

There is no separate CI group, because each engine's CI arrives with it:

- **PR 17** creates the matrix job (Postgres, required).
- **PRs 26 and 29** each add an entry, not required while the engine is Experimental.
- **Promotion** to Supported (ADR-0011) flips the required flag in repository settings.
- **The nightly matrix** (newest LTS or minor of each engine) is added with PR 17 and extended in PRs 26 and 29.
