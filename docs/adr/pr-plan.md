# Step plan (one upstream PR)

This file stays in the fork and never goes upstream. The user writes all PR titles and bodies, because Jellyfin forbids LLM-written direct communication. Nothing here is PR text.

## Rules

- **Branching.**
  - Within a group, every branch is cut from the one before it.
  - Group 0 starts from `upstream/master`.
  - Groups C and D both start from the tip of C0 and do not depend on each other, so their order can swap (ADR-0011).
  - Rows marked *independent* start from `upstream/master` and can be carved out at any time.
- **Every step:**
  - builds warning-free (`TreatWarningsAsErrors`);
  - passes the existing matrix;
  - leaves SQLite fully working.
- **Portability steps (group 0 and group C0):**
  - contain only provider-neutral changes, plus SQLite tests that pin the behaviour and follow the rules in ADR-0002;
  - add no references to any server engine.
- **SQLite migrations.** The **SQLite migration** column marks the only step that carries one. Every other step is SQLite-neutral: no migration file, and `HasPendingModelChanges` stays false.

**Local TDD.**

- The fork-only integration branch `feature/multi-db` carries the whole stack, the `docs/adr/` commits and work in progress.
- The test infrastructure and the providers are built there first, so a portability test can go red and then green on every engine before its step branch is cut.
- On `feature/multi-db`, portability tests derive from `JellyfinDbTestFixture`. On the group 0 step branches they derive from `SqliteDbTestFixture`, a one-token difference. Step 10 performs the rename.

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

These steps reconcile with the parity task list P1-P7. P8 is a Postgres item and moved to group B.

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 01 | `multidb/01-pin-ordering-and-grouping` | P1. Tests only: pin the Guid representative selection, string code-point order and NULL order (ADR-0006 (a), ADR-0007 (a)). No production change. | `tests/Jellyfin.Server.Implementations.Tests/Item/*` | ADR-0007 a1-a4, ADR-0006 a1-a4 | upstream/master | no |
| 02 | `multidb/02-shared-utc-converter` | P2. Shared `UtcDateTimeConverter` with no truncation (ADR-0004 R1); remove `SetDefaultDateTimeKind` from SQLite | `Providers.Sqlite/ValueConverters/DateTimeKindValueConverter.cs`, `Providers.Sqlite/ModelBuilderExtensions.cs`, `SqliteDatabaseProvider.cs:142-145`, `JellyfinDbContext.cs:336-340` | ADR-0004 a1 (R2), a2; `HasPendingModelChanges` false | 01 | no |
| 02b | `multidb/02b-utc-sentinels` | Store `DateTime.MinValue`/`MaxValue` as the same instant with UTC kind whatever their kind, through an internal helper on `UtcDateTimeConverter`; other values unchanged (ADR-0004). Found at the step 24 gate; built on `feature/multi-db` and replayed right after step 02. | `Jellyfin.Database.Implementations/ValueConverters/UtcDateTimeConverter.cs` | Converter unit tests and Unspecified/Local sentinel round trips, checked under five time zones on SQLite and PostgreSQL | 02 | no |
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
| 17 | `multidb/17-ci-db-matrix` | The `run-tests-db` matrix with only the Postgres entry, and the generic sentinel (ADR-0010 CI1-CI2); a nightly run on the newest PostgreSQL; the test container's storage layout for PostgreSQL 18 images | `.github/workflows/ci-tests.yml`, `.github/workflows/ci-tests-db.yml` (reusable), `.github/workflows/ci-tests-db-nightly.yml`, `tests/postgres.runsettings`, `PostgresTestBackend.cs`, `BackendLiveness.cs` | `ProviderBackend_IsLive` (supersedes the earlier name `DatabaseBackend_IsLive`), checked against `JELLYFIN_TEST_EXPECTED_PROVIDER`; a TRX check for the required tests | 16 | no |
| 18 | `multidb/18-integration-tests-on-postgres` | `JellyfinApplicationFactory` writes the selected provider's `database.xml` | `tests/Jellyfin.Server.Integration.Tests/JellyfinApplicationFactory.cs` | The integration suite on Postgres | 17 | no |

## Group C0: Shared prerequisites for further engines (provider-neutral)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 19 | `multidb/19-indexed-string-bounds` | Key-class `MaxLength`, `EnforceKeyLengths()`, ingest truncation of `Role`/`Value`, and the text-class annotation (ADR-0009 L1-L2). Snapshots are refreshed with no migration file (verified: zero SQLite operations). | Entities and `ModelConfiguration/*` for the 10 key-class columns, `JellyfinDbContext.cs`, `Item/PeopleRepository.cs`, `Item/ItemPersistenceService.cs`, both snapshots | ADR-0009 L5; model conformance (key budgets per engine); `HasPendingModelChanges` false on SQLite and PG | 18 | no (verified) |
| 20 | `multidb/20-normalised-match-columns` | Option E: `BaseItems.CleanOriginalTitle`, `Peoples.NameLower`, backfill routine; queries switch to these columns (ADR-0005 E1-E4) | Entities, `BaseItemMapper`, `SqlSearchProvider.cs`, `TranslateQuery.cs:268,273`, `PeopleRepository.cs`, new SQLite and PG migrations with the same ID, a new code routine | Tests 2 and 7 flipped; backfill routine test; E4 behaviours | 19 | **yes**: 2 × AddColumn, index swap, batched backfill |
| 20b | `multidb/20b-sort-name-ascii-fold` | Close the SortName gap without changing stored values (ADR-0005 R13): move the `NameStartsWith` filter from `ToLower().StartsWith` to `JellyfinDbFunctions.LikeIgnoreAsciiCase` with an escaped prefix pattern; keep the range filters on `ToLower()` and record the non-ASCII-term gap on engines whose `LOWER()` folds Unicode. No schema change, no routine, no public API, and no change in results on SQLite or PostgreSQL (ADR-0005 R14). | `BaseItemRepository.QueryBuilding.cs:228-246`, `LikePatterns.cs` | Parity tests for `NameStartsWith`, `NameStartsWithOrGreater` and `NameLessThan` with ASCII terms; known-gap ledger entry for a non-ASCII range term; `LikeIgnoreAsciiCaseTranslationTests` cases (É/é, Kelvin sign, dotted capital I, ß, ASCII mixed case) that every later provider must pass | 20 | no |
| 21 | `multidb/21-non-transactional-migration-guard` | `SupportsTransactionalMigrations` (default `true`) and the `migration-in-progress.json` marker (ADR-0003 S1) | `IJellyfinDatabaseProvider.cs`, `Jellyfin.Server/Migrations/JellyfinMigrationService.cs:546` | Marker lifecycle and startup refusal, using a test provider that returns `false` | 20 | no |
| 22 | `multidb/22-harness-leased-pool` | The generic leased-pool strategy: purge plus seed data, and `fresh: true` (ADR-0002 H1-H2) | `tests/Jellyfin.Database.Testing/*` | Pool self-tests on SQLite file databases | 21 | no |
| 22b | `multidb/22b-migration-workflow` | Contributor workflow for schema changes (ADR-0001 W1-W3). (a) A design-time `IMigrationsIdGenerator` on the PostgreSQL provider that reuses the id of the SQLite migration with the same name, found by file name in the SQLite provider's `Migrations/` (no assembly reference, ADR-0011), implementing the public interface (not the internal `MigrationsIdGenerator`: EF1001), registered with `[assembly: DesignTimeServicesReference]` so a different startup project still finds it. (b) One offline command that generates the SQLite migration first and then every other provider's. (c) One test theory over a provider table for pending model changes and id pairing, replacing the PostgreSQL-only class; every failure message names the command. (d) `src/Jellyfin.Database/readme.md` replaced by an "Adding a database change" page (the escape-hatch order of W4; review every generated migration; never hand-port SQL blindly). About 400 lines. | PostgreSQL provider `Migrations/`, the command, `PostgresMigrationBaselineTests.cs` (generalised), `src/Jellyfin.Database/readme.md` | The theory on SQLite and PostgreSQL; a generator test (same name reuses the SQLite id; a name with no SQLite migration gets a fresh id); the command run once in a throwaway checkout | 22 (cut from the pre-step-24 tip so it replays before group C) | no |
| 22c | `multidb/22c-migration-contents-check` | Cross-engine check of migration contents (ADR-0001 W2). For every paired id, compare the operations of `Up` and `Down` by kind, table and column, ignoring annotations. A `Sql` operation present on one engine only needs an allowlist entry with a reason, ledger-style. A fix for legacy SQLite data may stay SQLite-only; a fix for a bug in current code may not. | The test project of 22b, an allowlist file | The check on every paired migration so far, with an allowlist entry for each existing one-sided `Sql`. **Done 2026-10-15:** branch tip `70082dda51`, merged as `d59093e95c`, then `5597fca7cc`; see B27 | 22b | no |
| 22d | `multidb/22d-raw-sql-ban-and-kit` | The upstream half of the old step 27a (ADR-0001 W5): (a) banned APIs `IsSqlite`/`IsNpgsql`/`IsMySql`/`IsSqlServer`, and `ExecuteSqlRaw*`/`FromSql*`/`SqlQuery*` outside provider projects, each existing site suppressed with its reason; (b) the cross-engine conformance kit run on SQLite and PostgreSQL, replacing their per-provider purge test classes. Its rows: purge, fresh and identity; explicit identity insert and restore round trip; an induced write conflict through the classifier; a `ParentId` chain deeper than 15 and a folder delete; `MinValue`/`MaxValue` under a non-UTC `TZ`; LIKE `%`, `_` and non-ASCII; lists of 3,000 and 100,000 ids; a long composite key; model conformance. | `BannedSymbols.txt`, `tests/BannedSymbols.Tests.txt`, `tests/Jellyfin.Database.Testing/*` | The kit on SQLite and PostgreSQL. **Done 2026-10-15:** branch tip `580005950e`, merged as `2b3bcd0649`, see B28. As built, the ban is in `BannedSymbols.Database.txt`, applied by `Directory.Build.props` to production projects only (not the providers or `tests/`), and it also bans `GetDbConnection`, `ProviderName` and `ActiveProvider`. The MySQL purge test class stays until 27a | 22c | no |

## Group C: MySQL/MariaDB (Experimental, fork-only until ADR-0011 R1 is met; one provider, two matrix entries)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 23 | `multidb/23-efcore-10.0.12` | Bump every EF Core package and `HealthChecks.EntityFrameworkCore` to 10.0.12. Npgsql 10.0.3 still fits. | `Directory.Packages.props:36-45` | The existing suites | upstream/master (*independent*; carve early) | no |
| 24 | `multidb/24-mysql-provider` | `Jellyfin.Database.Providers.MySql` (Microting 10.0.12) and its members. Conventions: `utf8mb4_0900_bin` on both servers (ADR-0006 C4, superseding C2), `binary(16)` Guid (ADR-0007 G2), `datetime(6)` with the `MaxValue` clamp (ADR-0004 T3), `longtext` with prefix lengths (ADR-0009 L3), all filters removed (ADR-0001 M3), identifier limit 64, interceptor for strict mode and READ COMMITTED (ADR-0008 R3), retry codes, purge, `SupportsTransactionalMigrations = false`. Backends: `MySql`, `MariaDb` (pool). **Not registered.** Entry criteria from step 20b (ADR-0005 R13): `LikeIgnoreAsciiCase` translated with an ASCII-only fold, so that `LikeIgnoreAsciiCaseBehaviourTests` and `BaseItemRepositorySortNameFilterTests` pass on this provider's pooled backends; a known-gap ledger entry, keyed to this provider, for `NameStartsWithOrGreater`/`NameLessThan` with a non-ASCII term. | New project, `Directory.Packages.props` (`Microting.EntityFrameworkCore.MySql`, `Testcontainers.MySql`/`MariaDb` 4.16.0) | Member and classifier tests; the suite on both servers, with gaps recorded; model conformance | 20b, 22, 23 (step 23 dropped from the upstream series, see S9) | no |
| 24b | `multidb/24-dateplayed-fix` | Rewrite the `DatePlayed` sort without a derived table that references the outer item, which MariaDB cannot resolve. It changes a hot query on every engine, so it is its own step. It merges only if the SQLite and PostgreSQL plans and the benchmark do not regress; otherwise the MariaDB known gap stays. As built: no shared shape passed, so the shared query is unchanged and the MySQL provider rewrites the aggregate for MariaDB only (ADR-0011 F7). | `UnionAggregateInterceptor.cs` and its registration in the MySQL provider (planned: `Item/OrderMapper.cs`) | The existing null-order and DatePlayed tests on all four engines; own date, alternate version, both, neither, other user | 24 | no |
| 25 | `multidb/25-mysql-migrations-and-registration` | Baseline `20200101000000_InitialCreate` covering through step 20's migration; register `Jellyfin-MySQL` with the Experimental warning. Workflow rows (ADR-0001 W3): a design-time factory with a fixed server version, the id generator of 22b, MySQL rows in 22b's theory, command and 22c's check, and a pending-changes check with the MariaDB flavour. | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c) on both servers; safety-net test (S1); explicit `SHOW INDEX` prefixes | 24, 22b (the workflow rows only) | no |
| 26 | `multidb/26-ci-mysql-mariadb` | Add `MySql` and `MariaDb` matrix entries, not required, run on pull requests that touch database paths and nightly; the how-to per engine (configuration plus docker-compose) for MySQL/MariaDB and PostgreSQL from one template | `.github/workflows/ci-tests.yml`, the how-to pages | The sentinel on both | 25 | no |

## Group D: SQL Server (Experimental, fork-only unless maintainers ask; ADR-0011 R2)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 27a | `multidb/27a-conformance-kit` | The fork half of the old step 27a (ADR-0001 W5), before any SQL Server work (ADR-0001 E2-E3): (a) MySQL and MariaDB rows in the conformance kit of 22d, plus a LIKE `[` row on every engine; it replaces the MySQL purge test class. The `ParentId` row is where the step 24 pre-clear (M5) is proved on MySQL and MariaDB. (b) A test-side `EngineTraits` table replacing the engine switches in `PoolChecks`, `ModelRows`, `BackupServiceTests` and `JellyfinApplicationFactory`. (c) Escape `[` in the shared `LikePatterns` (motivated by SQL Server only, so it stays out of the upstream series). | `tests/Jellyfin.Database.Testing/*`, `LikePatterns.cs` | The kit on SQLite, PostgreSQL, MySQL and MariaDB | 22d, 26 | no |
| 27 | `multidb/27-sqlserver-provider` | `Jellyfin.Database.Providers.SqlServer` and its members (identity-insert scope, purge with `DELETE` + `RESEED`, retry codes, RCSI warning). Conventions: `binary(16)` Guid converter, BIN2 collation, `nvarchar(4000)` for text-class columns, OR filter dropped, `NoAction` on the 2 cascade FKs (ADR-0001 M5). Pool backend. **Not registered.** Written only after the SQL Server spike (ADR-0001 E3: the hazard checklist and the whole suite against a conventions-only stub) and after an ADR settles the text-class composite indexes over 1700 bytes (`Path`, `SortName`, `PresentationUniqueKey`). Entry criteria from step 20b (ADR-0005 R13): `LikeIgnoreAsciiCase` translated with an ASCII-only fold, so that `LikeIgnoreAsciiCaseBehaviourTests` and `BaseItemRepositorySortNameFilterTests` pass on this provider's pooled backends; a known-gap ledger entry, keyed to this provider, for `NameStartsWithOrGreater`/`NameLessThan` with a non-ASCII term. | New project, `Directory.Packages.props` (`Microsoft.EntityFrameworkCore.SqlServer` at the EF version, `Testcontainers.MsSql` 4.16.0) | Member tests; the suite with ADR-0006 G1/G2 and ADR-0009 G2 recorded; folder delete (M5); unique-NULL filter (M6) | 22, 27a | no |
| 28 | `multidb/28-sqlserver-migrations-and-registration` | Baseline and registration of `Jellyfin-SqlServer` with the Experimental warning | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c); failing-migration test | 27 | no |
| 29 | `multidb/29-ci-sqlserver` | Add the `SqlServer` matrix entry, not required (ADR-0010 CI3) | `.github/workflows/ci-tests.yml` | Sentinel | 28 | no |

## CI per engine

There is no separate CI group, because each engine's CI arrives with it:

- **step 17** creates the matrix job (Postgres, required).
- **steps 26 and 29** each add an entry, not required while the engine is Experimental.
- **Promotion** to Supported (ADR-0011) flips the required flag in repository settings.
- **The nightly matrix** (newest LTS or minor of each engine) is added with step 17 and extended in steps 26 and 29.

## Amendments (step 01 on Postgres, 2026-10-12)

Source: a scratch run of step 01's tests on Postgres over the harness. SQLite passed 32 of 32. On Postgres, 25 failed and all of them fell within existing gaps: `GuidAggregates` (42883) for 15 rows, `StringCollation` for 7 rows. `PeopleRepositoryDuplicateNameTests` passes.

- **B1. The harness stack carries the Group 0 tests.**
  - The harness branch is rebased onto the Group 0 tip as each Group 0 step is merged, starting with step 01.
  - On each rebase, the rename commit (A2) also switches the new test classes to `JellyfinDbTestFixture`.
  - The gap-ledger commit (step 13) wraps their failing spans. For step 01 that is 10 `KnownGap.On` sites, about +52/-26 over 6 files.
  - The `BaseItemRepositoryGroupingTests` conflict is resolved once: step 01's test bodies, inside the harness wrapper.
- **B2. `GuidAggregates` masks the ordering assertions on Postgres.**
  - Every `GetItemList` query that groups by presentation key fails on the missing `min(uuid)` before any ordering assertion runs. So on Postgres the 16 `NullOrder`, `StringOrder` and `NameRange` rows from step 01 test nothing about ordering until step 15 ships the aggregate.
  - **step 14** cannot cite those rows as evidence for the `"C"` collation or the `NULLS FIRST`/`LAST` generator. Its ADR-0006 `[ProviderFact]` checks must exercise ordering through a query path that does not group by presentation key. The step 14 author confirms that path exists before writing the tests.
  - **step 15** is where those rows first run their assertions on Postgres. step 15 reports every row that leaves `GuidAggregates` by name: either it passes, or the gap it moved to. "`GuidAggregates` closed" is not "ordering verified". The step 15 explainer says so.

- **B3. Harness rebase onto step 05 (`ccbb7d666e`, harness tip C6 `57760e8c33`).**
  - **The default `LikeIgnoreAsciiCase` translation is proven on Postgres.** `lower(col) LIKE p ESCAPE '\'` runs and passes in `SqlSearchProviderCaseMatchingTests` (9 rows), `ActivityManagerFilterTests` (13 rows) and the People `Bob`/`bob` dedupe. Replacing the default with a plain `LIKE` fails 21 of them on Postgres, so the tests do discriminate.
  - **`NameContains` and `SearchTerm` rows are masked by `GuidAggregates`** (42883 before any assertion). They join the B2 list: step 15 reports each of them by name, passing or moved to another gap.
  - **New gap `UnicodeLower`** (Postgres, assertion failures): the test database uses `en_US.utf8` (`datcollate`, verified), so `lower('É')` folds to `é` and the ASCII-only pinning tests (`Émile`/`émile` dedupe) fail. Under `"C"` `lower()` folds ASCII only, as on SQLite. **Step 14 must close it**, and its gap removal is checked like any other: the wrapped rows pass, or the step explains why not.
  - The SQLite-internals tests from step 05 (translation shape, equivalence grid, People case-sensitive `LIKE`) are marked SQLite-only in C5, the commit where the Postgres backend first exists. Accepted: before C5 there is no other provider to exclude.
  - Postgres totals at the harness tip: 1183 passed, 100 skipped (64 `GuidAggregates`, 12 Windows-only, 11 SQLite-only, 10 collation, 1 NULL order, 1 `UnicodeLower`, 1 no migrations), 0 failed, identical over two runs.

- **B4. Harness rebases are batched (2026-10-13).** The PostgreSQL track (steps 14-15) is built on the harness tip `57760e8c33` in parallel with Group 0. Rebasing the harness after every Group 0 step would move the base under that track each time, so the harness is rebased once Group 0 (through step 09) has landed, and the PostgreSQL track with it. That rebase adds the `NulInExactMatchParameter` gap from step 06 (ADR-0009 N3) and reports every Group 0 test on PostgreSQL, as B1-B3 require.

- **B5. PostgreSQL track results (steps 14-15, tips `e11a40f4ea` and `5579b02ee2`) and the items they hand on.**
  - The server runs on PostgreSQL 16.15 selected through `database.xml`: a fresh install migrates in one baseline, the wizard, authentication and the main API answer, a music library scan matches SQLite (including sort order and search), and a restart reuses the database. `GuidAggregates` (64 rows) and `PostgresMigrations` closed with every row passing; remaining PostgreSQL skips are Windows-only and SQLite-only tests.
  - **Classifier.** When the stack is rebased onto step 07, the PostgreSQL provider implements `IsRetryableWriteConflict` (23505, 40001, 40P01) with the unit tests ADR-0008 lists. The integration test `LibraryStructureControllerTests.RenameLibrary_*` failed 2 of 3 runs on PostgreSQL with a 23505 on `PK_BaseItems` (`ValidateTopLibraryFolders` racing the scan). After that rebase it is run ten times; if it still fails it is a real bug to fix, not a gap.
  - **Step 08** must cover a SQLite backup restored onto PostgreSQL end to end (history mapping, identity, NUL).
  - **Step 18** adds provider selection to `JellyfinApplicationFactory` (it never writes `database.xml`; an experiment patch exists) and a per-provider variant of `StartOverTests.DeletedDatabase_*`, whose refusal text differs on PostgreSQL.
  - **Step 16** (tests on the migrated schema) brings `IX_Peoples_NameLower` to model-source test databases.
  - **Benchmark.** ADR-0005 R1(ii) (SQLite against PostgreSQL after the conventions) and NULLS FIRST index use on large tables are measured before step 14 is merged. The run follows ADR-0005 R1a (2026-10-13): it no longer waits for a quiet machine; equal container/core budgets, at least three interleaved pairs, load recorded, plans decide and timings are indicative. The MySQL/MariaDB and SQL Server benchmarks follow the same rule.
  - The setup pre-marking bug the worker hit is X1, which the stack picks up on rebase.

## Amendments (single upstream PR, 2026-10-12)

The user changed the delivery strategy. This supersedes "small, stacked, independently mergeable PRs" wherever it appears in this file and in the ADRs.

- **S1. One upstream PR.**
  - The complete multi-database feature is built on the fork. When it is finished, it goes to jellyfin/jellyfin as one PR.
  - Nothing goes upstream piecemeal.
  - "PR NN" in the ADRs and explainers written before this date means step NN of this plan.
- **S2. Steps stay the units of work.** Groups 0, A, B, C0, C and D keep their order and numbering. For each step:
  - it gets its own branch, cut from the step before it;
  - tests come first;
  - it passes the gates: Code Reviewer, Debug and Release builds with no new warnings (ADR-0002 H7), and green tests;
  - it gets an explainer on `feature/multi-db`;
  - it is merged into `feature/multi-db` with `git merge --no-ff`.
  - Merging is never a reason to rebase or amend a step branch.
  - If a merged step later needs a review fix, its branch is fixed (it is unpushed) and the new tip is merged again. `feature/multi-db` history is never rewritten.
- **S3. The commit series is the deliverable.**
  - Reviewers will read the final PR commit by commit, and Jellyfin's LLM policy asks for large changes to be split into small, manageable commits.
  - Every commit must build and pass its tests on its own. Each has a clear imperative subject and a body that explains why.
  - A commit body may name the engine that motivates a change, for example PostgreSQL, but must not claim a SQLite fix that does not exist.
- **S4. Building the final branch.**
  - `multidb/upstream` is built by replaying the step branches' commits, in step order, onto the current `upstream/master`. It has no merge commits and none of the fork-only `docs/adr` commits.
  - That linear series is what goes upstream, when the user says so.
  - X1 (`fix/migration-setup-premark`) is an ordinary unit, merged like the rest, and its commit stays in the series. Maintainers may ask for unrelated fixes to be split out; the user decides at submission time.
- **S5. Upstream sync.**
  - Fetch `upstream` before starting every step, and at least between steps.
  - If `upstream/master` has moved:
    - rebase the step branch in progress (unpushed and unmerged, so this is safe);
    - merge `upstream/master` into `feature/multi-db` so conflicts surface early.
  - Merged step branches are rebased only when `multidb/upstream` is built (S4).
  - Report to the coordinator any upstream change to `src/Jellyfin.Database`, to the migrations, or to files the steps touch.
  - For every new upstream SQLite migration, generate the MySQL (and later SQL Server) migration with the step 22b command, then run the step 22c contents check. A one-sided `Sql` operation is either ported by hand or given an allowlist entry with its reason (ADR-0001 W6).
- **S6. Explainers.**
  - Write one per step, as now.
  - At the end, write an overall explainer for the whole PR, as study material and not PR text. It covers:
    - the architecture overview;
    - the behaviour changes on SQLite;
    - the test evidence;
    - known limitations and the experimental tiers;
    - a suggested reading order of the commits.
- **S7. Unchanged.** No pushing to origin or upstream, and no PR, until the user says so. The user writes all PR text.
- **S8. X1 is the exception and goes upstream now, as its own PR.** The user decided this on 2026-10-12.
  - `fix/migration-setup-premark` is published on origin at `f8bf42be78`. Its commits are `2d9b57c52d`, the fix, and `f8bf42be78`, the CONTRIBUTORS.md entry.
  - The branch is never rewritten. A change requested in upstream review goes on as a new commit, and the coordinator and the user handle that branch. Every new tip is merged into `feature/multi-db` again.
  - When `multidb/upstream` is built (S4), drop both commits if the PR has merged upstream by then, and keep them if it hasn't.
- **S9. Upstream watch list.** Check it at every upstream sync (S5), and again before `multidb/upstream` is built. A PR here that merges changes our work as noted. Recorded from the read-only scan at `61f9a9df23`.

| Upstream | State at scan | What it does | Our response |
|---|---|---|---|
| #17899 | open | Roll back failed database restores; the archive is validated first | Step 08's second commit (validate, transaction, in-transaction purge) overlaps it, and the later step 08 commits build on that commit, so it cannot simply be dropped. If #17899 lands, that commit is replaced by #17899 when the series is replayed and the conflicts in the later commits are resolved then (decided at the step 08 gate, 2026-10-13) |
| #17901 | open, depends on #17899 | `CompleteDatabaseRestoreAsync(JellyfinDbContext, CancellationToken)` on the provider | We use exactly this member (ADR-0003 U1) |
| #17119 | open | ItemValues get-or-create retry under parallel saves | `WriteConflictRetry` follows its shape (ADR-0008 U1). Step 07's last commit wraps `UpdateOrInsertItems`; if #17119 merges first, drop that commit (dropping it gives exactly commit 3's tree) and fix commit 3 so #17119's new test builds: `ItemPersistenceServiceItemValueRaceTests.cs:171-175` constructs `ItemPersistenceService` with 3 arguments and needs the provider argument added (CS7036). |
| #18091 | open | Foreign keys on TrickplayInfos/MediaSegments, new migration, `PRAGMA foreign_keys` | Regenerate the PG baseline and move the migration pin (ADR-0003 U3) |
| #16301 | open | SQLite-only `CleanValue` function for non-ASCII search | Conflicts with option E (ADR-0005 U3) |
| #18299 | open | Seeded random sort, `EF.Functions.Random` | Check in step 14 (ADR-0006 U1) |
| #17115 | open | SQLite pessimistic-lock rework | Re-check the locking default (ADR-0008 U3) |
| #17449 | open issue | `--restore-archive` crashes on a fresh instance | Covered by step 08 (ADR-0003 U1) |
| X1 (ours) | published on origin | Premark fix sent upstream on its own | S8 |
| X2 (ours) | local, not pushed | Premiere date read from the broadcast date tag (`ProbeResultNormalizer.cs:1658`) | S10 |
| X3 (ours) | local, not pushed | `GetLatestTvShowItems` subtracts 24 hours from `DateTime.MinValue` when a series' newest episode has no `DateCreated` (`BaseItemRepository.Querying.cs:343`) | Same handling as X2 (S10); B16 |
| X6 (ours) | locally merged with X5 in `a5f1e5b2c9`, postmerge proof green; upstream-shaped X6 `b4642f0dbf` follows X5 `7ce3511a84` on `aec42b345e`; fork pair `cecac3f9b5`, `47b844674b` (B23) | `20250420070000_MigrateActivityLogDb.cs:87` resets `sqlite_sequence` for `'ActivityLog'`, but the EF table is `ActivityLogs` (`20200514181226_AddActivityLog.cs:17`), so the reset after `RemoveRange` does nothing and re-imported entries continue from the old highest id | One-word fix with its own failing-first test, like X3; can be offered upstream alongside X5 |
| X7 (ours) | upstream-shaped `837cf78032` (`fix/hls-extinf-invariant-culture`) on `aec42b345e`, local and unpushed; fork `68ea0d1249`, merged by `0f118c6b7c` with green post-merge proof; that merge is on origin `feature/multi-db`, pushed by the coordinator, and PR #1's CI there was green as relayed (B22) | `SubtitleController.GetSubtitlePlaylist` appended `TimeSpan.TotalSeconds` with the current culture, so fr-FR gave `#EXTINF:4,5,` | Invariant formatting with a failing-first fr-FR test; can be offered upstream on its own |
| X8 (ours) | upstream-shaped `34dc00973a` (`fix/vtt-timestamp-map-header-only`) on `aec42b345e`, local and unpushed; fork `96b3b718a9`, merged by `5d8b85e832` with green post-merge proof; that merge is on origin `feature/multi-db`, pushed by the coordinator, and PR #1's CI there was green as relayed (B22) | `SubtitleController.GetSubtitle` replaced every `WEBVTT`, splitting cue text and header text with the timestamp map | Map only after the whole first line, with whole-output tests; can be offered upstream on its own |
| X10 (ours) | upstream-shaped `2084aa42b2` (`fix/backup-home-sections`) on `aec42b345e`, local and unpushed; fork `939887782c` (`multidb/x10-merge-candidate`) on `3d0ab14734`, fast-forwarded into `feature/multi-db` and on origin `feature/multi-db`, pushed by the coordinator, checked read-only with `git ls-remote` on 2026-10-15 (relayed); no CI result at `939887782c` claimed (B29) | `HomeSection` had no `DbSet`, so a backup did not carry it and a restore did not give the home-screen sections back on any engine (22d finding F1) | A singular `DbSet`, and on upstream a restore that keeps archive keys and reads them before the purge; can be offered upstream on its own |
| #18381 | open | Rewrites the `recentCutoff` line in `GetLatestTvShowItems` | Would reintroduce the X3 throw if it lands without X3; rebase X3 or ask for the null check there |
| #18423 | open | Adds `BaseItemRepositoryLatestItemListTests.cs` | A reviewer may want X3's tests moved into it |
| #17879, #17878 | #17879 MERGED upstream on 2026-10-13 (`619eb231b4`, upstream/master `e53a6d3e6a`), merged into the fork by the 2026-10-15 sync (B18); #17878 (dotnet-ef 10.0.13) open at the 2026-10-15 scan | #17879 moved the Microsoft pins to 10.0.12, Test.Sdk to 18.11.0 and System.Text.Json to 10.0.13, a superset of step 23 | Drop step 23 when `multidb/upstream` is built. The replay of the later steps must not touch those pins. Step 23 left the dotnet-ef tool at 10.0.11; step 22b (`d4d55bcada`) moved the fork's tool to 10.0.12, which the sync keeps (upstream's tool is still 10.0.11). |
| EF Core pins (step 23) | `f84294103c` bumps EF, Sqlite, HealthChecks and four Microsoft.Extensions pins to 10.0.12 | Upstream's dependency bot has bumped these (#17879 merged 2026-10-13, EF at 10.0.12) | At each sync and before `multidb/upstream`: if upstream is already at or past 10.0.12, drop step 23; if it moved some pins only, re-cut it against the new pins. The MySQL provider needs EF >= 10.0.12. |
| Our code routines and migrations | step 06 adds `20261012120000_RemoveNulFromItemKeys`; step 19 adds `20261013090000_NormalizeOverlongKeys`; step 20 adds the EF migration `20261013120000_AddNormalisedMatchColumns` (SQLite and PostgreSQL, same ID) and the routine `20261013130000_BackfillNormalisedMatchColumns`, which must run after that migration | Upstream's latest routine at `e53a6d3e6a` is `20260915120000_MigrateRatingLevels`; its latest SQLite migration is `20261002210711_MakeOwnerIdIndexesPartial` | Before `multidb/upstream` is built, every routine and EF migration the series adds must sort after upstream's latest; if upstream adds a later one, re-date ours, keeping the step 20 migration before its backfill routine. Check how each interleaves with EF migrations on SQLite and that it is pre-marked on a fresh PostgreSQL install. |

  - Also re-run the scan's searches at each sync: `git grep -i -E "postgres|npgsql|mysql|mariadb"` on `upstream/master`, plus the commit log under `src/Jellyfin.Database`, `Jellyfin.Server.Implementations/Item`, `Jellyfin.Server/Migrations` and `FullSystemBackup` since the last sync.
  - The scan of 2026-10-15 adds the rows in B23.
- **S10. X2, `fix/probe-broadcast-date`, is an independent unit like X1.** The user ruled on 2026-10-12 that it is fixed as part of this work.
  - The bug: `ProbeResultNormalizer.cs:1658` looked up `WM/MediaOriginalBroadcastDateTime` but parsed the year tag, so the broadcast date never became the premiere date. Details are in ADR-0004 U3.
  - The branch is cut from `upstream/master`, not stacked on the steps, so it can be submitted on its own. It has one commit with the fix and its test.
  - It goes through the usual gates and gets an explainer. It is then merged into `feature/multi-db` with `--no-ff`.
  - Whether it ships inside the single PR or separately is decided at submission time, as for X1. Until then it is not pushed.
- **S11. Decision ownership (user direction, 2026-10-12).** The orchestrator owns every technical decision, including rework-round limits; nothing technical goes to the user. When a step is stuck: step back, get an architect's view, decide, record it in an ADR, and continue. Policy that stays with the user and is not raised again: pushing to any remote; opening or posting anything upstream (Jellyfin's policy requires the user's own words); deleting branches. Backup branches are kept and listed once in the final report after the upstream series is built and verified. Progress goes to the coordinator at milestones only: a step merged, the harness merged, a provider working, the final series ready.
- **S12. Definition of done (user, 2026-10-12).** "Build and make sure that Jellyfin can support multiple databases such as SQLite, MySQL, Postgres, MariaDB, and other databases, and have one branch with all of these changes in it."
  - The deliverable is `feature/multi-db`: one branch on which Jellyfin runs on SQLite, PostgreSQL, MySQL, MariaDB and SQL Server (CockroachDB stays rejected, ADR-0011). Each provider is selectable through `database.xml`, migrates a fresh database, starts the server, and passes the cross-provider parity suite and the integration tests on its engine.
  - ADR-0011's tiers (Experimental, fork-only) only decide what is offered upstream later. They do not reduce what is built here: MySQL, MariaDB and SQL Server are implemented, tested and green on `feature/multi-db`, like PostgreSQL.
  - Also part of done: a how-to in the fork for running Jellyfin on each engine (configuration plus a docker-compose example per engine), and a CI matrix on the fork covering every engine.
  - **Pace.** Work is parallelised wherever dependencies allow. Provider tracks (PostgreSQL conventions and baseline, then MySQL/MariaDB and SQL Server) are prepared in their own worktrees on top of the harness while Group 0 finishes, and are rebased onto the proper base when it lands. The aim is a working server on every engine early, then hardening.
  - Milestones reported: Group 0 complete; harness merged; each engine reaching "server starts and the parity suite is green"; the final upstream series and the overall explainer.
- **S13. Step 09 is dropped (ADR-0001 P1).** The SQLite-era routines with engine SQL never run outside SQLite: they are pre-marked on fresh installs and carried by restore. Group 0 is steps 01-08. The step numbering of later steps is unchanged; step 10 follows step 08.

## Amendments (step 14/15 gate, 2026-10-13)

- **B6. Step 14 delta PASS; step 15 PASS WITH NITS, one must-fix decided by building step 12 now.**
  - Step 15's provider message tells users to set `database:ConnectionString` (`JELLYFIN_DATABASE__CONNECTIONSTRING`), which nothing reads yet. Rather than ship a message that points at nothing, or drop it and re-add it later, step 12 (connection-string override, ADR-0001 option 3b) is built now from the Group 0 tip. The override is applied to a copy of the database options, so no later save can persist it.
  - Order on the fork: harness rebase (steps 10/13 with the PostgreSQL classifier and identity reseeding) and step 12 merge into `feature/multi-db`; steps 14/15 are then rebased onto that tip with the step 15 nits folded in: B2 log "backed up as" only when a real backup key exists, B3 a 73-column body line in `1c855cf282`, B4 `PostgresMigrationBaseline` and `PostgresUuidAggregate` internal.
  - In the final linear series step 12 precedes steps 13-15, so the message is true at every commit that carries it.
- **B7. The PostgreSQL benchmark adds one commit to step 14 (ADR-0003 R4, ADR-0005 R7).** During the 14/15 rebase, a commit that sets `jit = off` per physical connection goes right after the UTC session commit, with its tests. Step 14 merges only after a JIT-off re-run shows S1-S6 within 3x of SQLite, or after a recorded decision on whatever remains.
- **B8. Step 11 input: test classes that still open SQLite themselves (harness tip `d22e50d315`).** Step 11 converts them to the provider fixture, or marks them SQLite-only with a reason if they test SQLite internals. Implementations.Tests: `Item/ItemCountServiceTests`, `Item/ItemPersistenceWriteConflictTests`, `Library/UserDataManagerTests`, `Security/AuthenticationManagerTests`, `Users/UserManagerNormalizedUsernameTests`, `Users/UserManagerProfileImageTests`, `Users/UserManagerUpdateUserTests`, `Data/LikeIgnoreAsciiCaseTranslationTests`, and the parts of `FullSystemBackup/BackupServiceTests` that build their own connection. Server.Tests migration routines: `ConsolidateLocalizedUserViewsTests`, `HarmonizeConflictingUserDataTests`, `MigrateLinkedChildrenStaleItemsTests`, `RemoveNulFromItemKeysTests`, `RepairAlternateVersionLinksTests`. Server.Tests has no PostgreSQL coverage until then. Already SQLite-internal by design: `SqliteDatabaseProviderPurgeTests`, `SqliteWriteConflictClassificationTests`.
- **B9. Step 14 adds one provider interface member (ADR-0003 R5).** A default no-op member on `IJellyfinDatabaseProvider` (`Jellyfin.Database.Implementations`, on the ABI list, so it is permanent), called once per start after schema migrations. The PostgreSQL provider uses it to set `jit = off` for its own role in its own database. It replaces the R4 connection initializer and `No Reset On Close`; the test backend change made for the initializer (template built without pooling, `ClearPool` removed) is dropped with it.
- **B10. PostgreSQL milestone met (2026-10-13, `c265d3834f`).** With step 15 merged, Jellyfin starts on PostgreSQL 16.15 selected through `database.xml` or `database:ConnectionString`. A fresh database is migrated by the baseline, and JIT is turned off for the role once. The wizard, authentication, a library scan, search and sort work, and the data persists across a restart. A malformed connection string fails with guidance and without echoing it. The parity suite is green: 1581 passed, 49 skipped, 0 failed, identical over two runs in container mode, so the `pg_dump` restore tests ran. The skips are 33 SQLite-only, 12 Windows-only and 4 NulInExactMatchParameter. The 4 NUL rows are the only remaining PostgreSQL gap, to be fixed at the API boundary outside this series. The integration suite on PostgreSQL arrives with step 18.
- **B11. Step 16 amends B5: model-source test databases do not get `IX_Peoples_NameLower` (2026-10-13).**
  - `TestSchemaSource.Model` means exactly the schema EF derives from the model. The default is now `Migrations`, so every test that depends on the index, or on the PostgreSQL `min(uuid)` aggregate, runs on a migrated schema. A model-source database carrying part of the migrated schema would describe neither schema, so the PostgreSQL model template no longer installs the aggregate either.
  - `IX_Peoples_NameLower` is `lower("Name")` with no NULL order. `Peoples.Name` is NOT NULL, so `lower("Name")` is never NULL and the NULL order of the index cannot affect any plan. The step 14 rule (nullable index columns sort NULL first) does not apply to it. The test that checks the rule excludes this index by name for that reason, and the baseline is unchanged.
- **B12. PostgreSQL integration suite green (2026-10-13, `6dcae752eb`).** This closes the open item in B10. With step 18 merged, the integration test server runs on the selected provider. With PostgreSQL selected, the factory creates an empty database and writes `database.xml`, and the server's own migrations build the schema. In container mode, twice: Integration 139 passed, 6 skipped, 0 failed. The skips are 3 upstream `UserLibraryControllerTests` and 3 SQLite-only. Implementations 1584 passed, 50 skipped; Server 92 passed. The four StartOver PostgreSQL cases (database dropped or empty, wizard reset or completed) and the factory provider proof passed both times. Remaining in the PostgreSQL track: step 17 (CI) and step 11 (hand-rolled tests onto the fixture).
- **B13. PostgreSQL track complete (2026-10-13, `fccca748c7`).** Steps 11 to 18 and step 23 are merged on `feature/multi-db`, on top of the test harness. Every test class runs on the selected provider, or is SQLite-only with a stated reason, and test projects can no longer open SQLite directly. In container mode with `postgres.runsettings` and the expected provider set, run twice: Implementations 1608 passed, 51 skipped; Server 80 passed, 12 skipped; Integration 139 passed, 6 skipped; 0 failed. The sentinel, the `pg_dump` restore test and the integration provider proof all pass, on PostgreSQL 16.15. On SQLite: 1577/85, 85/7, 132/11. The CI database job (PostgreSQL 16.15 on pull requests, 18.6 nightly) passes in local simulation but has never run on GitHub, which needs a push to the fork. The only remaining PostgreSQL gap is `NulInExactMatchParameter`. Next: C0 (steps 19 to 22), then MySQL/MariaDB and SQL Server.
- **B14. Step 24 input from the step 21 gate (2026-10-13).** `BackupService` (around `:214`) calls `MigrateAsync` while restoring an archive, outside `JellyfinMigrationService`. On a provider whose migrations are not transactional, a failure there leaves a partial schema and no `migration-in-progress.json` marker. Step 24 must send that call through the marker logic, or refuse an archive restore that needs migrations on such a provider. The step 24 provider also needs a real pre-migration backup. Until it has one, `MigrationBackupFast` returns the empty key, and the operator's own `mysqldump`/`mariadb-dump` is the only recovery path. The refusal message already says so.
- **B15. Entry criteria for the pooled backends, from the step 22 gate (2026-10-13).** Steps 24 and 27 must do the following before their backends lease from the pool:
  - **Seed through EF.** Insert seed data through EF's own migration pipeline instead of the hand-rolled insert in `DatabaseReset`: `IMigrationsModelDiffer.GetDifferences(null, designTimeModel.GetRelationalModel())`, filtered to `InsertDataOperation`, then `IMigrationsSqlGenerator` and `IMigrationCommandExecutor`. This handles identity seeds (SQL Server `IDENTITY_INSERT`) and owned or shared-type entities.
  - **Restart identity per engine.**
    - SQL Server: `DBCC CHECKIDENT(RESEED, 0)` gives a next id of 0 on a table that never had a row, so only reseed tables whose `last_value` is not NULL.
    - MySQL/MariaDB: `TRUNCATE` with `FOREIGN_KEY_CHECKS=0`, or `DELETE` plus `AUTO_INCREMENT = 1`.
  - **Run the conformance checks on every pooled backend.** Run "reset contents match a fresh database" and "identity restarts at 1" against `TestDatabaseEnvironment.Backend` whenever it is pooled, not only in the SQLite file self-tests.
  - **Schema fingerprint.** Each engine supplies its own fingerprint (step 22 ships the SQLite one), so a lease returned with a changed schema is dropped.
  - **Prove the `fresh` flag reaches the backend.** The step 22 re-gate found that `DatabaseTestFixture` passing `false` instead of the caller's `fresh` still passed every test, because the SQLite and PostgreSQL backends ignore it. With the first pooled backend behind the fixture, add a test at the fixture seam showing that a `fresh` fixture gets a database that is not from the pool and is dropped afterwards.
  - **Pool teardown nits from the re-gate.** Fold in when the pool gets its first real user: the `DatabaseReset` comment names `GetSchema()`; `Lease` must not hand out an idle database a concurrent `Dispose` has already swept; the `AggregateException` from a failed drop after disposal goes through the same path as other post-disposal drops; a `fresh` lease does not compute a fingerprint it never uses.

## Amendments (third-engine evaluation, 2026-10-14)

- **B16. Plan changes from the architecture evaluation (ADR-0001 E1-E5).**
  - **Step 24** finishes its fix round as planned.
  - **Step 24b** is the `DatePlayed` rewrite, split out of step 24 because it changes a hot query on every engine. The evaluation flags several correlated subqueries per row as a SQLite performance risk, so it merges only with SQLite and PostgreSQL plans and a benchmark that show no regression. Otherwise the MariaDB known gap stays and is documented for users.
  - **Step 02b** (UTC sentinels) and **X3** (`fix/latest-episodes-null-date`, from `upstream/master`, a null `DateCreated` crashing the latest-episodes query) are new independent units, found while gating step 24.
  - **Steps 25 and 26** stay as planned. Step 26 also writes the PostgreSQL how-to, which does not exist yet, from the same template as the MySQL/MariaDB one.
  - **Step 27a** (conformance kit, `EngineTraits`, banned APIs, `[` escape) comes before any SQL Server work. Step 27's brief follows the SQL Server spike and an ADR on text-class composite indexes over 1700 bytes. The evaluation names that index question as the largest SQL Server risk.
  - **Corrections for the SQL Server plan.**
    - `OwnerId` is already `NoAction` (`BaseItemConfiguration.cs:40`). Error 1785 concerns the `ParentId` self-cascade and the two `AncestorIds` cascades, which M5 settles.
    - EF adds the unique-NULL filter itself (M6).
    - The 2100-parameter limit only applies if SQL Server copies MySQL's one-parameter-per-value lists. It keeps the `EF.Parameter` path (OPENJSON).
    - New items: escape `[` in LIKE patterns (step 27a), and `LOWER` folding Unicode under BIN2 collations.
  - **No rework of merged SQLite and PostgreSQL steps** (ADR-0001 E4).

## Amendments (contributor-workflow evaluation, 2026-10-14)

- **B17. How a later contributor's database change reaches every engine (ADR-0001 W1-W7).** The project owner asked whether a contributor has to implement each feature per database. An architecture evaluation sampled 242 upstream database commits from the last year. 77% touched no migration, entity, routine, raw SQL or `EF.Functions` call and need nothing per engine. Of 21 checked by hand, 11 needed nothing, 4 needed only generated migrations and 6 needed engine judgement. About 40% of upstream migrations contain hand-written SQLite SQL, and nothing compared migrations across engines. It also proved the design-time id generator on EF 10.0.12.
  - **New upstream-bound steps in group C0:** 22b (id generator, one command, provider-table theory, guide), 22c (migration-contents check with allowlist) and 22d (raw-SQL ban and the conformance kit on SQLite and PostgreSQL). The old step 27a is split: its upstream half is 22d, and 27a keeps `EngineTraits`, the MySQL/MariaDB kit rows and the `[` escape.
  - **Branch bases.** 22b is cut from the `feature/multi-db` tip before step 24 is merged (the commit that records this amendment), and 22c and 22d from the step before each. Their commits then replay before group C in `multidb/upstream` without edits. They are merged into `feature/multi-db` after steps 24 and 24b, and any conflict with group C is resolved in the merge commit, never by rewriting a merged step.
  - **Order.** Steps 24 and 24b merge first. Then 22b and step 25 run in parallel. Step 25's baseline, registration and migration tests do not need 22b. Its workflow rows (MySQL in the theory, the command and the contents check) are its last commits, written after 22b merges; step 25 is unmerged, so it is rebased onto `feature/multi-db` for them (S5). 22c follows 22b, and 22d follows 22c. 27a follows 22d and 26, and stays before any SQL Server work.
  - **Correction to the orchestrator's first view.** MySQL index prefix lengths are model annotations set by a convention, so EF generates them. The hand edits a generated migration can need are data SQL, expression indexes, renames (EF scaffolds drop plus add) and type changes that need `USING`.
  - **For the owner's list, not acted on:** whether the PostgreSQL leg becomes a required check upstream; CODEOWNERS for `src/Jellyfin.Database/**` and the gap ledger; a database checklist in the PR template (`.github/` is core-owned); whether upstream accepts a second migrations set at all; in-tree versus plugin providers (already listed, ADR-0001 E5).

## Amendments (step 24, 2026-10-14)

- **Per-commit builds (2026-10-14).** `dotnet build tests/<project> --no-incremental` fails at every commit, including the base, with CS0234 for `MediaBrowser.Model.MimeTypes` (`MediaBrowser.Model/Net/MimeTypes.cs:170,196`). The `MimeTypes` 2.5.2 package compiles its type in from source, and a single-project rebuild leaves it out. A plain project build and `dotnet build Jellyfin.sln --no-incremental` are not affected. So per-commit scripts build each commit in a new worktree, or delete every `bin` and `obj` folder first, and then run a plain `dotnet build tests/<project>`.

## Amendments (upstream sync and step 25, 2026-10-15)

- **B18. S5 lapsed; the gated sync `81f29427e6` corrects it.**
  - **The lapse.** `upstream/master` had not been merged into `feature/multi-db` since `61f9a9df23`. The S9 row for #17879 recorded upstream at `e53a6d3e6a` on 2026-10-13, but nothing was merged. It was a lapse of S5, not a deliberate deferral.
  - **The merge.** `81f29427e6` merges upstream `aec42b345e` (7 commits, 15 paths) into `d38fccdf44`. Upstream changed nothing under `src/Jellyfin.Database`, `Jellyfin.Server.Implementations/Item`, `Jellyfin.Server/Migrations`, `FullSystemBackup` or CI, and added no SQLite migration or routine. So nothing was generated or re-dated.
  - **Pure apart from its conflict resolutions.** Four files differ from both parents:
    - `Directory.Packages.props`, the one conflict: upstream's Microsoft 10.0.12, Test.Sdk 18.11.0 and System.Text.Json 10.0.13 are taken; the fork's provider, Testcontainers and xUnit pins are kept;
    - `CONTRIBUTORS.md`, with both names in the shared hunk;
    - `ProbeResultNormalizer.cs` and its tests, auto-merged with X2 and upstream #18243 side by side.
  - **Proof.** `/tmp/upstream-sync-20261015/run.Gqpr1O8j`: 17 gates rc 0, complete. Passed/skipped, 0 failed:
    - SQLite 2314/103, 152/8, 137/11;
    - PostgreSQL 2346/68, 146/14, Integration 144/6;
    - MySQL and MariaDB 2251/147 and 104/55;
    - MediaEncoding 105/1.

    Debug warnings 211 unique, identical to the `d38fccdf44` baseline. A static review approved the merge as pure. Details: `explainers/sync-upstream-20261015.md`.
- **B19. ci-fix-2, the positive-control lesson and cross-platform CI.**
  - **The lesson.** ci-fix-1 merged at `cec9b58f17` on one APPROVE WITH NITS review. A second review judged its handle tests not approvable on test strength: parallel `ClearAllPools`, a catch-all `IOException`, and no positive control. A missing positive control on a negative-only assertion is a blocker, not a nit, even when one mutant run passes.
  - **The fix.** ci-fix-2 (test-only, merged `d38fccdf44`) isolates the handle tests from process-wide pool clearing, adds a positive control, and checks the `.old` renames on every platform. Linux negative controls A, B and C are in `/tmp/ci-fix-2/negative`.
  - **Post-merge proof** (`/tmp/ci-fix-2/postmerge`): 11 gates rc 0. Results match the sync proof above for SQLite, PostgreSQL Implementations/Server and both MySQL-family engines.
  - **Fork CI at `d38fccdf44`** (`/tmp/ci-pr1/windows3.log`). Windows: Server.Tests 151/9, Implementations 2289/128, Integration 137/11, 0 failed. Among the handle tests only the Linux `/proc` positive control (`OpenLegacyFiles_FileHeldOpen_ListsTheFile`) is skipped. Format, Ubuntu, macOS, PostgreSQL, ABI, CodeQL and OpenAPI were relayed green.
  - **Fork CI at the sync tip `81f29427e6`** was relayed all green: format, Ubuntu, macOS, Windows, the PostgreSQL job, ABI (no difference) and CodeQL. No local log of that run was kept.
- **B20. Step 25 as built.**
  - **Branch.** Nine commits, rebased onto `81f29427e6` with `range-diff` 9/9 identical; the pre-sync tip is kept on `multidb/25-before-sync-rebase` (`1f6dfa2e50`). Merged with `--no-ff` as `abf45dcad2`; the merge tree equals the tip tree.
  - **Definitive proof** `/tmp/step25/definitive.V40kdWS7`. Every per-commit leg of all nine commits passed, and so did every tip gate before the container check: Release 0/0, format, the four engines, PostgreSQL twice, MariaDB 11.8 advisory 2346/137 on 11.8.9, Debug 211 equal to the sync base, the unskip reconciliation (31 passed + 4 baseline-covered skips per engine) and the `dev/add-migration` workflow run. Its rc 59 abort was a false positive of the container gate (B25). The ending HEAD and status were checked afterwards.
  - **Post-merge proof** `/tmp/step25/postmerge`: 14 gates rc 0, complete, Release 0/0. Passed/skipped, 0 failed:

    | Engine | Implementations | Server.Tests | Integration |
    |---|---|---|---|
    | SQLite | 2386/111 | 152/9 | 137/11 |
    | PostgreSQL 16.15 | 2418/76 | 146/15 | 144/6 |
    | MySQL 8.4.11 | 2346/137 | 126/35 | 144/6 |
    | MariaDB 11.4.13 | 2346/137 | 126/35 | 144/6 |

    MySQL-family Server.Tests rose from 104 to 126 passed, and the integration suite now runs on both servers.
  - **Fork CI at `abf45dcad2`** was relayed green on every job: format, Ubuntu, macOS, Windows (8m46s), the PostgreSQL job (10m58s), ABI (no difference) and CodeQL. No local log of that run was kept.
  - **Server.Tests provenance is companion-based.** Server.Tests has no sentinel of its own. A provider run of it is attributed to its engine by the Implementations sentinel of the same run, plus the audited provider selection in `Jellyfin.Database.Testing`. Reports and explainers say so until B21 lands.
  - **Gate follow-ups:**
    - the MySQL copy of `PairedMigrationsIdGenerator`, with its parity tests, was accepted until 22c. **Done in 22c (B27):** one shared internal generator in `Jellyfin.Database.Implementations.DesignTime`; the 22c merge `d59093e95c` moves MySQL to it and removes the copy, `MySqlPairedMigrationsIdGeneratorTests` and `ProviderSourceParityTests`;
    - in the replay, the version-detection commit becomes a fixup into step 24;
    - 27a puts the history-in-fingerprint rule into the pool contract and moves the provider-to-assembly switch out of `ProviderRequirement.MigrationIds`;
    - `OfflineContexts` leaves the PostgreSQL test namespace;
    - the first MySQL migration after the baseline needs an upgrade test from a baseline-only database;
    - document that the transactional wrapper tests service behaviour only;
    - step 26 adds MariaDB 11.8 as an advisory CI entry.
  - Details: `explainers/25-mysql-migrations-and-registration.md`.
- **B21. New 22d item (upstream half): a sentinel in Server.Tests.** **Done in 22d (`580005950e`, B28):** Server.Tests provenance is now proven by its own sentinel on every engine, not by its companion runs.
  - **The fact.** `ProviderBackend_IsLive` in `tests/Jellyfin.Server.Tests`, through `BackendLiveness.Verify`, printing the actual backend and server version. Every provider run of Server.Tests then proves its own backend.
  - **Negative control.** A run with the wrong `JELLYFIN_TEST_EXPECTED_PROVIDER` must fail.
  - **Scope.** Step 25's approved commits and the frozen harness stay unchanged.
- **B22. X7, X8 and X9, under the standing rule that small upstream bugs are fixed inside this work (S10).**
  - **When.** They start after the sync and step 25, both done, and do not block either. One at a time, like X3 and X5: an upstream-shaped branch from `upstream/master` plus a fork merge candidate, failing test first, per-commit and final-tip proofs, independent review, `--no-ff` merge, post-merge proof. Before each is cut, check upstream for an existing fix and check open #17012 and #16393 for overlap.
  - **X7, culture-sensitive `#EXTINF`** (`SubtitleController`, already present at `61f9a9df23`). The test sets `CultureInfo.CurrentCulture` to fr-FR and restores it. With 45,000,000 ticks and segment length 10 it first fails showing `#EXTINF:4,5`, and passes with invariant formatting.
  - **X8, global `WEBVTT` replace** (already present at `61f9a9df23`). The failing test has the cue "Say WEBVTT aloud". The fix touches only the header line and leaves the cue text unchanged.
  - **X9, the raw-bytes return for an unsupported charset** (new upstream with #18309). Conditional: it is fixed only if a runtime test with a real ISO-8859-16 SRT, or whatever the detector actually reports as unsupported, shows garbled output where the earlier code rejected the file. Static reading or a mocked detector is not enough. If it does not reproduce, it stays on the watch list.
    - **Closed on 2026-10-15: not reproduced as a #18309 regression; no fix in this series.** A runtime probe (`/tmp/x9-probe/`) ran 25 generated SRT sample files on disk (with expected text), through the real `SubtitleEncoder`, `SubtitleEditParser` and UTF.Unknown 2.7.0 on .NET 10.0.12. It was built once without the #18309 guard (`out-pre/`, `run-pre-all.log`) and once with it (`out-post/`, `run-post-all.log`). The source commits of the two builds were not retained; the pre/post split rests on the guard's "returning stream as-is" log appearing only in the post build. FFmpeg (`IMediaEncoder`) was mocked, which does not affect this path: a parser-supported external SRT never reaches FFmpeg.
      - **Detector.** UTF.Unknown reports `iso-8859-16` for the Romanian, Croatian and Polish samples and `iso-8859-10` for the Latvian and Lithuanian samples, each with a null `Encoding` (`inventory.log`: `Encoding.GetEncoding` rejects both names). The Hungarian and French ISO-8859-16 samples are detected as `iso-8859-2` and `iso-8859-1`, so they never reach the guard. Only those five files log "returning stream as-is".
      - **Before #18309 the output was already garbled.** `new StreamReader(stream, null)` does not throw on .NET 10; it falls back to UTF-8 (`srnull.log`: no exception, CurrentEncoding utf-8, 232 U+FFFD, the same count as the Romanian sample's output). Neither build threw for any file. B22's premise, that the earlier code rejected the file, does not hold.
      - **After #18309.** For the five files, srt to vtt output is byte-identical to before. srt to srt now returns the raw ISO bytes, identical to the input file, instead of UTF-8 with U+FFFD. That is a different failure, not clearly a worse one. All other outputs are byte-identical between the two builds.
      - **Decision.** The real defect, garbled text for charsets .NET cannot decode, predates #18309. Fixing it means routing those files through FFmpeg's `-sub_charenc` (iconv), a subtitle-pipeline change outside the multi-database scope. Both points are upstream-note candidates in B26.
      - **Housekeeping.** The `x9-probe` worktree was removed (it was clean); `/tmp/x9-probe/` is kept as evidence.
  - **P3: the duplicate ISO-8859-7 test** from #18309. Recorded only.
  - **Status on 2026-10-15: X7 and X8 locally merged and proven; X9 closed without a fix.** Details and evidence paths are in [the X7/X8 explainer](explainers/x7-x8-subtitles.md).
    - **X7.** Upstream-shaped `837cf78032` on `aec42b345e`; fork `68ea0d1249`, the same patch (patch-id `48443ff0ef`). Red: expected `#EXTINF:4.5,`, actual `#EXTINF:4,5,`. Merged by `0f118c6b7c` (parents `37f86395df`, `68ea0d1249`). Post-merge `/tmp/x7x8/postmerge/postmerge-run.yfdrTbhq/`: 16/16 gates rc 0, API 166, MediaEncoding 105/1, provider rows 11,212 (10,619 passed, 593 skipped), zero failures. Relayed CI at `0f118c6b7c`: format, the three platform test jobs, the PostgreSQL database job, ABI and CodeQL succeeded; PostgreSQL is CI's only database job.
    - **X8.** Upstream-shaped `34dc00973a` on `aec42b345e`, a message-only amend of `cd12b4dcdc` (tree `f1be4647ed`); v1 `0e3275af52` split header text after the signature and was replaced. Red 5 failed / 1 passed on both shapes, green 6/6. Fork `96b3b718a9` drops only the `using System.Text;` that X7 already added. Merged by `5d8b85e832` (tree `cd096ce8f9`, identical to the candidate). Post-merge `/tmp/x7x8/postmerge/x8-postmerge-run.q05sdoYe/`: 16/16 gates rc 0, 14 TRX files, 11,490 rows (10,896 passed, 594 skipped), zero failures. Both merges are on origin `feature/multi-db`, pushed by the coordinator (checked read-only with `git ls-remote`); the orchestrator and the docs writer pushed nothing. CI at `5d8b85e832`, relayed from the orchestrator's read-only `gh` read of the check runs: `format-check`, `run-tests` on `ubuntu-latest`, `macos-latest` and `windows-latest`, `run-tests-db` for PostgreSQL, all ABI jobs (BASE, HEAD, Difference, Common Ancestor, Generate Report, Base/Head Artifact) and CodeQL with Analyze (csharp) succeeded; the housekeeping jobs (Rename, Rebase, Release project board, main) were skipped. No CI log is kept locally. CI's only database job is PostgreSQL, so the MySQL and MariaDB evidence for this merge remains the local post-merge matrix. Origin `feature/multi-db` is now `be39fc9afb`, pushed by the coordinator and checked with `git ls-remote`; from then on, docs-only commits are pushed together with the next code merge. The upstream-shaped branches are local and unpushed, and nothing has been offered upstream.
    - **Both.** Every recorded Release build has 0 warnings and 0 errors; each fork candidate's Debug comparison has 211 warnings on each side and an empty diff. The coordinator reports independent static APPROVE at both shapes of each fix. Server.Tests provenance remains companion-only (B21). Bare-CR WebVTT is unsupported, and non-`WEBVTT` input gets no map, by design.
    - **X9** was closed after a runtime probe with the real charset detector: not reproduced as a #18309 regression, no fix in this series (see the X9 bullet above, and B26).
- **B23. S9 watch list, read-only scan of 2026-10-15.**

  | Upstream | State at scan | Our response |
  |---|---|---|
  | #17299, #18309 | merged 2026-10-13 (`e53a6d3e6a`; backport `f624cb4cc5`), in the fork since B18 | Source of X7-X9 triage; no upstream follow-up for the raw-bytes return, EXTINF culture or WEBVTT replace yet. X7 and X8 are fixed in the fork; X9 is closed without a fix (B22). #18309's comment and test are recorded as upstream-note candidates (B26) |
  | #17012 | open; touches `SubtitleEncoder` only | Skips charset detection for WebVTT; check for overlap before X9. Checked read-only by the orchestrator on 2026-10-15: still open; it changes only `SubtitleEncoder`'s charset return, adding a WebVTT and confidence >= 0.5 gate. It does not address encodings .NET cannot open, or #18309's comment. Merge-conflict risk only |
  | #16393 | open; touches `SubtitleEncoder` and tests | External subtitle loading for STRM; check for overlap before X9. Checked read-only by the orchestrator on 2026-10-15: still open; it changes only the protocol passed to `DetectCharset` and touches neither B26 point. Merge-conflict risk only |
  | #17878, #18417, #18365, #17644 | Renovate, all open | dotnet-ef 10.0.13 (touches tools), SharpCompress 0.50.5, a packaging action, and xUnit v4. #17644 would collide with the fork's `xunit.v3` pins |
  | #18381, #18423 | open, updated 2026-10-14 | Touch the X3 area (the `recentCutoff` line and its tests); responses as in S9 |
  | #17879 | merged (`619eb231b4`) | Supersedes step 23; drop step 23 in the final replay |

  X6 is added to the S9 table. No other watched PR had landed.
  - **X5/X6 status, superseded on 2026-10-15.** Both are locally merged by `a5f1e5b2c91dd9c3b0f9c0e5a364ac396c980108`, parents `39026f46fb479fb4977b16f3f0b9ce9468f3554b` and fork tip `47b844674bd058d8fcb1a659e569a6ba96cd1039`. The upstream-shaped pair is `7ce3511a84`, `b4642f0dbf` on `aec42b345e`; the fork pair is `cecac3f9b5`, `47b844674b` on `abf45dcad2`. Both rebase range-diffs are unchanged (`=`). The coordinator reports independent static APPROVE, no blockers. This supersedes the earlier not-merged/proof-running status, not the distinction between a fork merge and an upstream merge.
  - **Verified proof.** Premerge `/tmp/x5/rebase-20261015/run.080031.2985950/` passes per-commit Release (zero warnings/errors), format and tests; X5/X6 negative controls show the intended failures. Each branch shape's exact-base/final-tip Debug comparison has 211 warnings on each side and an empty normalized diff. Postmerge `/tmp/x5/postmerge/run.GmZeHdZp/` is COMPLETE, all 14 gates rc 0, Release zero warnings/errors, exact format and 12 suites with zero failures. Independent TRX counting confirms 11,212 result rows. Passed/skipped (Implementations; Server.Tests; Integration): SQLite **2386/111; 156/9; 137/11**; PostgreSQL 16.15 **2418/76; 146/18; 144/6**; MySQL 8.4.11 and MariaDB 11.4.13 each **2346/137; 126/38; 144/6**. All four Implementations and Integration sentinels pass; Server.Tests remains companion-only. Start/end identities and clean statuses match the exact merge; Docker added/removed/new-labelled reconciliation lists are empty. Fork CI on PR #1 at head `a5f1e5b2c9`, relayed from the orchestrator's read-only `gh api` read of the check runs: `format-check`, `run-tests` on Ubuntu, macOS and Windows, `run-tests-db` for PostgreSQL, the ABI jobs and CodeQL (Analyze csharp) all completed with success; the workflow-housekeeping jobs were skipped. No CI log is kept locally. CI's only database job is PostgreSQL, so the MySQL and MariaDB evidence for this merge remains the local postmerge matrix. See [the X5/X6 explainer](explainers/x5-activitylog-import.md).
  - **Limitation.** The X5 fix does not repair a server whose earlier upgrade already dropped activity entries and recorded the migration as applied. Those entries survive only in `activitylog.db.old`. Any recovery path is deferred, to later or to upstream, and is not part of the fix.
- **B24. Legacy-migration issues: upstream-note candidates, not fixed in this series.**
  - **Crash idempotence.** `MigrateDisplayPreferencesDb` and `MigrateAuthenticationDb` save the target rows before they archive the source and before the migration history row is written. A run interrupted in between imports again on the next start and can hit unique indexes.
  - **Swallowed rename failure.** The four legacy import routines (activity log, users, display preferences, authentication) catch the `IOException` from the archive rename and only log it. The migration is still recorded as applied, so a failed rename is silent and never retried.
  - The owner may raise these upstream.
- **B25. Proof-process lessons.**
  - **Positive controls in harness audits.** A harness audit includes a full-suite TRX positive control, not only targeted subsets. Step 25's first definitive attempt stopped (rc 23) because the validator assumed one TRX result per test definition, and xUnit theories with non-serialisable data produce several results under one definition.
  - **Child processes must not inherit the heavy lock.** A `VBCSCompiler` started by a build inherited fd 9 and held `/tmp/multidb-heavy.lock` through its idle timeout, so the next launch was refused (rc 89). Children now close fd 9 (`exec 9>&-`, or `flock -o`).
  - **Container gates use Testcontainers labels.** On a shared machine, any new `docker ps -a` identity is not ours to judge. The gate counts only new identities with the `org.testcontainers` or session labels, and records the rest in `container-unrelated-new.txt`. Still to fold in: a short wait for our Ryuk to exit before the final snapshot.
  - **Format workspace notice.** `dotnet format --verbosity diagnostic` on the X7 candidate exited 0 and formatted 0 files. Its one load diagnostic is the Debug-only analyzer `ProjectReference` to `Jellyfin.CodeAnalysis.csproj` with no matching metadata reference (`Directory.Build.props:23-24`). It is a notice, not a format failure, and it is not fixed. Evidence: `/tmp/x7x8/fork-x7.Ep7y8RGB/format-diagnostics/`.
  - **Long runs need an exit waiter.** A watch that expires after 30 minutes missed step 25's 03:46 exit by about 4 hours. Use a background exit waiter (up to 2 hours), or re-arm the watch on every expiry.
- **B26. Subtitle charset issues: upstream-note candidates, not fixed in this series.** Notes for the owner, from the X9 probe (B22); no upstream text is written here.
  - **Charsets .NET cannot decode.** External text subtitles in ISO-8859-16 or ISO-8859-10 come out garbled, because .NET has no encoding for either. This affects Romanian, Croatian, Polish, Latvian and Lithuanian files. On this path the parser-supported SRT is decoded in-process and never reaches FFmpeg: the only place the C# code adds `-sub_charenc` is `ConvertTextSubtitleToSrtInternal` (`SubtitleEncoder.cs:441`, the flag at `:462`), which is reached for formats the parser does not support (`SubtitleEncoder.cs:249-250`). The probable fix is to hand these files to FFmpeg with `-sub_charenc`, which #18309's comment assumes already happens. The defect predates #18309.
  - **#18309's comment and test.** The comment at `SubtitleEncoder.cs:192` ("passed to ffmpeg via -sub_charenc if needed") is wrong for this path. Its test, `GetSubtitleStream_UnsupportedEncoding_DoesNotThrow` (`SubtitleEncoderTests.cs:273`), writes ISO-8859-7, which is detected as supported, so it never reaches the new branch (see P3 in B22).
  - Open #17012 and #16393 overlap only as merge conflicts (B23). The owner may raise these upstream.

## Amendments (step 22c, 2026-10-15)

- **B27. Step 22c, the migration contents check, as built.** Details and evidence paths are in [the 22c explainer](explainers/22c-migration-contents-check.md).
  - **Branches.**
    - **Upstream-shaped.** `multidb/22c-migration-contents-check` on `d37f0d08fb`:
      - `d95815c0cc` moves the paired id generator and the SQLite migrations finder into `Jellyfin.Database.Implementations.DesignTime`, as internal types, with no new `InternalsVisibleTo`;
      - `ac04957b95` adds the check;
      - `70082dda51` updates the readme.
    - **Fork.** The merge is `d59093e95c` (parents `693da13de8`, `70082dda51`), and `5597fca7cc` compares one row per migrations assembly, with MySQL and MariaDB collapsed as "MySQL 8.4".
    - **State.** The orchestrator fast-forwarded `feature/multi-db` to `5597fca7cc`. That code tip is on origin `feature/multi-db`, pushed by the coordinator; the orchestrator checked this read-only with `git ls-remote` on 2026-10-15 (relayed). The docs commit `ed644cf583` and its follow-up go up with the next code merge. CI at `5597fca7cc` was later green, as relayed (see CI below).
    - **Backups, kept.** A: `-backup-a` `788a28775a`, `-backup-b` `d1ff0939dd`. Fork `multidb/22c-merge-candidate`: `-backup-a` `ed39922868`, `-backup-b` `7452d0826e`, `-backup-c` `1f61eb54d9`.
  - **The merge resolves the group C conflicts, as B17 allows.**
    - MySQL moves to the shared generator. The copy, `MySqlPairedMigrationsIdGeneratorTests` and `ProviderSourceParityTests` go, which settles B20's first gate follow-up.
    - It adds the fork-only category `BaselineCoverage`, accepted only for a row's `BaselineId`.
    - It adds 10 `20200101000000_InitialCreate` entries: 7 `BaselineCoverage` and 3 `EngineSpecific`.
    - The sync `81f29427e6` brought no migrations.
  - **What the check compares.** For every id that more than one migrations assembly has, it compares the offline `Up` and `Down` operations:
    - by kind, table and columns, with nullability, uniqueness and foreign-key on-delete;
    - for `CreateTable`, each column and key separately;
    - for SQL, the first 16 hex digits of the SHA-256 of the white-space-normalised text, plus a 60-character excerpt.
  - **Allowlist rules.** `MigrationContentsAllowlist.json` is an exact-witness allowlist.
    - The check fails on missing, stale, ambiguous and invalid entries, and on parse errors, which are wrapped. Every failure names `dev/add-migration`.
    - `LegacySqliteData` is allowed for SQLite only, and `BaselineCoverage` only for a row's `BaselineId`.
  - **What the check does not compare.** Annotations, types and maximum lengths, defaults and computed SQL, collations, index filters, check-constraint SQL, names of new indexes and constraints, data values, and operation order. A hand edit to one of these in one provider's migration body is caught by neither this check nor the pending-changes check, which compares the snapshot with the model.
  - **Allowlist as merged.**
    - **Fork:** 17 entries:
      - 7 `BaselineCoverage`;
      - 4 `EngineSpecific`: MySQL `AlterDatabase` utf8mb4, the PostgreSQL `min(uuid)` aggregate create and drop, and the PostgreSQL-only `DROP DEFAULT`;
      - 6 `EquivalentSql`.
    - **A:** 7 entries.
    - **Tables:** `/tmp/22c/allowlist/`.
    - **`DROP DEFAULT`.** SQLite keeps the `''` default on `Peoples.NameLower`, because it cannot drop a default without a table rebuild. EF Core writes the column on every insert, so this is an observation, not a bug, and the PostgreSQL drop is `EngineSpecific`.
    - **`AddNormalisedMatchColumns` `Down`.** The independent reviewer hand-checked that the three SQLite SQL statements act on the same objects, in the same order, as PostgreSQL's `DropIndex` and `DropColumn` operations, and recomputed all six hashes (the reviewer's reasoning, relayed).
  - **Correction to B17's estimate.** 12 of the fork's 57 SQLite migrations contain `migrationBuilder.Sql`, not about 40%. Only `20261013120000_AddNormalisedMatchColumns` is paired. Every earlier SQLite migration is covered by the PostgreSQL and MySQL baselines, so it is not paired or compared.
  - **Review.** Relayed; the notes are not in the evidence.
    - **First review: FAIL.** Blocker B1: a parse-time `JsonException` did not name the command. Should-fix: S1 to S3, C-S1 and the docs.
    - **Re-review: APPROVE** on both shapes.
    - **N-A1** was then folded in as a comment-only change, proven by `/tmp/22c/chain3/NA1-*.diff`, which equal the git diffs between the round 2 and final tips.
    - **Not done.** N-A2 (a missing allowlist file is not wrapped) and N-A3 (some tests do not assert the command) are optional follow-ups.
  - **Commit-chain rule.** No commit may be red by design, because the upstream series is reviewed commit by commit.
    - The chain was rewritten and every commit gated (`/tmp/22c/chain3/gates/results.tsv`, `COMPLETE`, rc 0).
    - **Incident.** One gate run was interrupted by an accidental `TaskStop`, during the format gate of the round 1 merge `5774b18c61`. It is kept as `/tmp/22c/superseded/chain-round1/gates-interrupted-1/`, and was rerun before that chain was superseded.
  - **Format gate and replay rule.**
    - **Branch A's format gate is "equal to base":** the same 6 errors as `d37f0d08fb`, 2 × SA1611 in `BaseItemRepositorySortNameFilterTests.cs` and 4 × SA1615 in `PostgresMigrationAtomicityTests.cs`.
    - **Fork commits must reach rc 0.**
    - **Replay rule.** In `multidb/upstream`, fold `62282ffd55` as a fixup into the steps that introduced those files, `bcdd6b9259` and `651c8dd720`. Evidence: `/tmp/22c/format-probe/DECISION.txt`.
  - **Proofs.**
    - **Fork `5597fca7cc`** (`/tmp/22c/fork-proof/`): 14/14 rc 0, Release 0/0, format 0. Passed/skipped (Implementations; Server.Tests; Integration):
      - SQLite **2408/111; 156/9; 137/11**;
      - PostgreSQL 16.15 **2440/76; 146/18; 144/6**;
      - MySQL 8.4.11 and MariaDB 11.4.13 each **2368/137; 126/38; 144/6**.

      11,300 TRX rows: 10,707 passed, 593 skipped. I recounted them, and the orchestrator recounted them independently. Sentinels pass; Server.Tests provenance is companion-only (B21). Docker clean; identity exact and clean.
    - **Branch A `70082dda51`** (`/tmp/22c/A-proof/`):
      - SQLite **2065/87; 144/8; 137/11**;
      - PostgreSQL **2097/52; 139/13; 144/6**.

      4,903 TRX rows: 4,726 passed, 177 skipped. Format is equal to base.
    - **Debug** (`/tmp/22c/fork-debug/`, `/tmp/22c/A-debug/`): 211 against 211 on each shape, with empty diffs.
    - **Negative controls** (`/tmp/22c/negative/{fork,A}`): missing, stale (an extra and an altered entry), the six normalised `Down` entries removed, ambiguous, and an unknown category.
      - Each fails as expected and names `dev/add-migration`, and each run is restored to green.
      - Fork run 1, with a bug in the script's search string, is kept in `fork-run1-needle-bug/` and noted.
    - **Parity** (`/tmp/22c/parity/fork/`): `dev/add-migration` gave the same id on SQLite, PostgreSQL and MySQL. A name with no SQLite twin got fresh ids on PostgreSQL and MySQL.
    - **Per-commit gates:** `/tmp/22c/chain3/gates/results.tsv`.
  - **CI.** CI at `5597fca7cc`, relayed from the orchestrator's read-only `gh` read of the check runs, all succeeded: `format-check`, `run-tests` on Ubuntu, macOS and Windows, `run-tests-db` for PostgreSQL, all ABI jobs (`ABI - Difference` confirms that the internal `DesignTime` types change no checked API) and CodeQL with Analyze (csharp); the housekeeping jobs were skipped. No CI log is kept locally. CI's only database job is PostgreSQL, so the MySQL and MariaDB evidence is the local matrix.

## Amendments (step 22d, 2026-10-15)

- **B28. Step 22d, the raw SQL ban and the conformance kit, as built.** Details and evidence paths are in [the 22d explainer](explainers/22d-raw-sql-ban-and-kit.md).
  - **Branches.**
    - **Upstream-shaped.** `multidb/22d-raw-sql-ban-and-kit` on `70082dda51`:
      - `98dcacbfc6`, the ban;
      - `cddbb29d77`, which moves `TemporaryTimeZone`;
      - `6ba81682b7`, the kit;
      - `d6b1d8169b`, which replaces the purge tests;
      - `580005950e`, the Server sentinel.
    - **Backups:** `-backup-a` `131c6caf67`, `-b` `924f969d87`, `-c` `df6c59aaa1`, `-d` `5ca59314c4`.
    - **Fork merge.** `2b3bcd0649` (parents `1847099b6c`, `580005950e`, tree `9a09b376f6`). The orchestrator fast-forwarded `feature/multi-db` to it. It is on origin `feature/multi-db`, pushed by the coordinator, and was checked read-only with `git ls-remote` on 2026-10-15 (relayed). The 22d docs commits go up with the next code merge. No CI result at `2b3bcd0649` is claimed.
  - **Ban.**
    - `BannedSymbols.Database.txt` has 25 ids, verified against the package XML (`/tmp/22d/ban-ids/`).
    - It is applied by path: to every project except those under `tests/` and `Jellyfin.Database.Providers.*`. There is no opt-out property, because MSBuild imports environment variables as properties.
    - `DatabaseBannedSymbolsTests` resolves the ids by reflection; pinned exemptions apply only on `FileNotFoundException`.
    - **Suppressions.** 7 production suppressions, each scoped. On the fork, `MigrateLibraryDb` has a scoped pragma at `:85-91`, and its file-wide disable is removed. 4 unused file-wide disables were removed.
    - **Still hidden.** 20 file-wide `RS0030` disables still hide the ban. This is follow-up 1, for 27a, because analyzer 5.6.0 has only `RS0030`.
  - **Scope decision.** The implementer reported 109 suppressions in the first version: 7 in production and 102 in tests. My union of `/tmp/22d/dev/ban-sites*.txt` gives 107 under `tests/`. The difference is unreconciled, and neither figure is preferred. The coordinator narrowed the ban to production code, because tests may be engine-specific by design.
  - **Kit.**
    - 55 cases: Purge 6, Restore 2, WriteConflict 4, FolderDelete 2, DateTimeLimits 12, Like 18, IdList 8, CompositeKey 1, Model 2. The brief said Purge 5; the TRX files show 6, which makes 55.
    - SQLite: 53 passed, 2 skipped by `SQLITE-CONTAINS-PARAMETER-LIMIT`. PostgreSQL: 55 passed. MySQL and MariaDB: all skipped until 27a, 23 skipped results each.
    - The restore row has self-expiring exceptions for `HomeSection` and the concurrency tokens. No fork ids appear in shipped text (H10).
  - **B21 done.**
    - The Server.Tests `ProviderBackend_IsLive` runs on every engine. Server.Tests provenance is now independently proven, not companion-based.
    - Negative controls: a wrong expected provider fails on A and on the fork, including MySQL against MariaDB in both directions.
  - **Review.** Relayed; `/tmp/22d/review/` keeps the range-diffs, not the notes.
    - The kit and the other commits passed review first time.
    - **Ban S1, a real bug.** The SQL Server `MigrationBuilder` id named the wrong namespace and matched nothing.
    - **Other ban changes.**
      - The environment-variable opt-out was replaced by the path rule.
      - The pinned exemption was narrowed to `FileNotFoundException`.
      - `GetDbConnection`, `ProviderName` and `ActiveProvider` are now banned.
    - The A evidence was rerun.
    - **Final:** APPROVE at `580005950e` and APPROVE at `2b3bcd0649`.
    - **Process slip.** One `git add -A -- tests/` was used; nothing unintended was committed.
  - **Proofs.** Passed/skipped (Implementations; Server.Tests; Integration):
    - **A `580005950e`:**
      - SQLite **2118/88; 145/8; 137/11**;
      - PostgreSQL **2152/50; 140/13; 144/6**.

      5,012 TRX rows (4,836 passed, 176 skipped). Format equal to base.
    - **Fork `2b3bcd0649`:**
      - SQLite **2461/112; 157/9; 137/11**;
      - PostgreSQL 16.15 **2495/74; 147/18; 144/6**;
      - MySQL 8.4.11 and MariaDB 11.4.13 each **2370/157; 127/38; 144/6**.

      11,455 rows (10,823 Passed, 632 NotExecuted), recounted by me and independently by the orchestrator.
    - **Both:** all three sentinels pass. Debug 211/211 on both shapes, empty diff. Per-commit gates are in `/tmp/22d/chain-{A,fork}`.
  - **Negative controls** (`/tmp/22d/negative/`):
    - **Server sentinel.** A wrong expected provider fails.
    - **Kit.** The broken purge and restore rows fail.
    - **Ban file.** A wrong MySQL id fails.
    - **Ban injection.** It fires in production even with the environment variable set, and is silent in tests and providers. `ItemPersistenceService` is not reported (follow-up 1).
    - **Masked run.** Purge-mutation run 1 was masked on PostgreSQL; that is F4.
  - **Findings.**
    - **F1 = X10.** `HomeSection` is lost on restore on every engine. **Fixed by X10** (`939887782c`, B29); the kit's `HomeSection` exception is removed.
    - **F2.** Concurrency tokens are restored as 0. Upstream-note candidate; no concrete harm.
    - **F3.** A plain `Contains` with 100k ids fails on SQLite. Upstream-note candidates, at `580005950e`: `LinkedChildrenService.cs:93`, `BaseItemRepository.TranslateQuery.cs:319`, and `ItemPersistenceService.cs:403`, `457`, `531` and `601`.
    - **F4.** A partial purge differs. PostgreSQL uses `TRUNCATE ... CASCADE`; SQLite turns foreign keys off and leaves orphans. Every current caller purges all tables. Contract note or 27a kit row.
  - **Follow-ups**, as numbered in the implementer's report. Items 2 to 10 are the implementer's words, verbatim, as relayed:
    1. The 20 file-wide `RS0030` disables; a dedicated analyzer rule is planned for 27a.
    2. Banned-symbol test TypeId: a generic declaring type is rendered wrongly and indexers aren't handled. Both can only cause a false failure.
    3. The ban is Debug-only, which is the existing pattern.
    4. ModelConformance compares the model with itself on SQLite, so it's meaningful only on PG.
    5. The two SQLite-only routines (MigrateActivityLogDb, StripEmbeddedLinkedChildren) have no guard against another engine.
    6. The CI sentinel check should be per project (step 26).
    7. 27a: the sentinel doesn't tell MySQL from MariaDB. Fix it by comparing the server-reported flavour (MariaDB marker) with the selected provider, plus a negative control, using EngineTraits.
    8. The commit body could name MySqlRestoreIdentityTests and MySqlPurgeDatabaseArgumentTests (optional, not done).
    9. 27a: check whether the kit duplicates those MySQL classes.
    10. H10: no fork ids in shipped text.
  - **22c CI.** Green at `5597fca7cc`, as relayed (B27).
  - **CI at `2b3bcd0649`.** CI at `2b3bcd0649`, relayed from the orchestrator's read-only `gh` read of the check runs, all succeeded: `format-check`, `run-tests` on Ubuntu, macOS and Windows, `run-tests-db` for PostgreSQL, all ABI jobs and CodeQL with Analyze (csharp); the housekeeping jobs were skipped. No CI log is kept locally. The ABI jobs build `Jellyfin.Server` in Debug (`dotnet build Jellyfin.Server -o ./out`, `ci-compat.yml`), where the production ban is active, so their passing shows no unsuppressed banned use in `Jellyfin.Server` and its dependencies; that covers that build only, not the whole solution. The PostgreSQL job log shows `Jellyfin.Server.Tests` at 147 passed, 18 skipped, 0 failed, matching the local run where the Server sentinel passed. CI's sentinel check greps all TRX files of a job together (`ci-tests-db.yml`), and TRX artifacts are uploaded only on failure or cancellation, so CI alone does not prove the Server sentinel independently; step 26 makes the check per project. CI's only database job is PostgreSQL.

## Amendments (X10, 2026-10-15)

- **B29. X10, the home sections in backup and restore.** Details and evidence paths are in [the X10 explainer](explainers/x10-backup-home-sections.md).
  - **Bug.** `HomeSection` had no `DbSet`, and `BackupService` backs up and restores sets only.
    - On SQLite and PostgreSQL, the restore's purge cascaded from `DisplayPreferences` and deleted the sections.
    - On MySQL and MariaDB, the purge runs with `foreign_key_checks = 0`. The sections survived the purge, and the restore left the post-backup sections in place, possibly orphaned.
    - Either way, a restore did not give the sections back. The 22d kit found this (B28 F1).
  - **Upstream-shaped fix.** `fix/backup-home-sections` `2084aa42b2` on `aec42b345e`; `-backup-a` `9f4da47963`.
    - **A singular set.** `public DbSet<HomeSection> HomeSection`. A plural name would rename the table and need a migration. Backup keys entries by table name and restore looks them up by set name, so a mismatch would be skipped silently.
    - **Archive keys kept.** `System.Text.Json` does not set private setters, so `DisplayPreferences` ids were regenerated and the sections' foreign keys dangled. With the set alone, the restore failed on a foreign key after the tables were emptied.
    - **Read before the purge.** Keys are read and validated before the purge, and keyless sets are guarded (none exist).
    - **Caveat in the body.** Restore now writes explicit identity values. That is correct on SQLite, which raises `sqlite_sequence`. Upstream has no `IDENTITY_INSERT` for SQL Server, and on PostgreSQL the sequences would be left behind.
  - **Fork.** `multidb/x10-merge-candidate` `939887782c` on `3d0ab14734`; `-backup-a` `8ef5f18861`, the commit before a message-only reword, with the same tree `c2591c7402`.
    - **What it adds.** Only the set and the test, because the fork's `BackupService` already reads keys before the purge and restores generated keys plus identity hooks.
    - **The kit.** `RestoreConformance`'s not-in-the-backup list is removed; `HomeSection` was its only entry. The RowVersion exception remains and still expires on its own.
    - **The reword.** The body originally said "deleted on every engine", which is wrong for MySQL and MariaDB.
    - **State.** The orchestrator fast-forwarded `feature/multi-db` to `939887782c`. It is on origin `feature/multi-db`, pushed by the coordinator, and was checked read-only with `git ls-remote` on 2026-10-15 (relayed). The upstream-shaped branch stays local and unpushed. The docs commits go up with the next code merge. No CI result at `939887782c` is claimed.
  - **Failing first.**
    - **A.** The base with the tests only fails both new tests. The no-op-restore and no-pre-purge-read mutations fail as expected.
    - **Fork.** The base with the test only fails on all four engines. The kept-gap kit row fails on SQLite and PostgreSQL with the "remove the table" message.
  - **Gates.**
    - **A.** Release 0/0; format rc 0 (the base is rc 0); SQLite Implementations 1090/1078/0 failed/12 skipped; Server.Tests 39/39.
    - **Fork** (run at `8ef5f18861`, same tree). Release 0/0, format 0; 12 legs, total/passed/skipped:
      - SQLite **2574/2462/112, 166/157/9, 148/137/11**;
      - PostgreSQL **2570/2496/74, 165/147/18, 150/144/6**;
      - MySQL and MariaDB each **2528/2371/157, 165/127/38, 150/144/6**.

      11,459 rows: 10,827 Passed, 632 NotExecuted. I recounted them, and the orchestrator audited them independently. All three sentinels pass on every engine. Debug 211/211, empty diff.
    - **Reworded commit.** The tree is identical, plus a Release and `BackupServiceTests` sanity check (65/65).
  - **Next ids** (scratch, uncommitted, `/tmp/x10/identity-scratch/v3`). After a restore, new ids were above the restored maximum on all four engines. The PostgreSQL negative control without the completion hook collided (23505).
  - **Backup compatibility.** The engine version stays 0.2.0.
    - An old archive on a new server restores as before, without sections.
    - A new archive on an old server ignores the `HomeSection` entry, without error.
    - A version bump would make servers refuse each other's archives.
  - **Review** (relayed). First review: APPROVE with S1 (the body), plus test hardening and the pre-purge read. Re-review: APPROVE on both shapes.
  - **Notes.**
    - **Upstream restore order.** Upstream restores Config and Data before a database restore it may refuse. This is pre-existing; the fork checks first. Upstream follow-up.
    - **Two reads.** Every entry is now read twice, streamed.
    - **MySQL and MariaDB.** The kit rows do not run there, so the `HomeSection` identity has no committed coverage there. For 27a.
    - **A harmless `!`.** The fork's `GetGeneratedKeys` still uses `FindPrimaryKey()!`.
  - **22d CI.** Green at `2b3bcd0649`, as relayed (B28).
