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
| 25 | `multidb/25-mysql-migrations-and-registration` | Baseline `20200101000000_InitialCreate` covering through step 20's migration; register `Jellyfin-MySQL` with the Experimental warning | Provider `Migrations/`, `ServiceCollectionExtensions.cs:24-27` | Migration tests (a)-(c) on both servers; safety-net test (S1); explicit `SHOW INDEX` prefixes | 24 | no |
| 26 | `multidb/26-ci-mysql-mariadb` | Add `MySql` and `MariaDb` matrix entries, not required | `.github/workflows/ci-tests.yml` | The sentinel on both | 25 | no |

## Group D: SQL Server (Experimental, fork-only unless maintainers ask; ADR-0011 R2)

| # | Branch | Scope | Key files | Tests added | Depends on | SQLite migration |
|---|---|---|---|---|---|---|
| 27 | `multidb/27-sqlserver-provider` | `Jellyfin.Database.Providers.SqlServer` and its members (identity-insert scope, purge with `DELETE` + `RESEED`, retry codes, RCSI warning). Conventions: `binary(16)` Guid converter, BIN2 collation, `nvarchar(4000)` for text-class columns, OR filter dropped, `NoAction` on the 2 cascade FKs (ADR-0001 M5). Pool backend. **Not registered.** | New project, `Directory.Packages.props` (`Microsoft.EntityFrameworkCore.SqlServer` at the EF version, `Testcontainers.MsSql` 4.16.0) | Member tests; the suite with ADR-0006 G1/G2 and ADR-0009 G2 recorded; folder delete (M5); unique-NULL filter (M6) | 22 | no |
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
| #17879, #17878 | open (Renovate), #17879 mergeable | #17879 moves all 18 Microsoft pins to 10.0.12 plus Test.Sdk 18.10.1 (a superset of step 23); #17878 bumps the dotnet-ef tool | If #17879 merges, drop step 23. At submission the user may point to them instead of carrying step 23. Step 23 leaves the dotnet-ef tool at 10.0.11 on purpose (it only prints a version warning). |
| EF Core pins (step 23) | `f84294103c` bumps EF, Sqlite, HealthChecks and four Microsoft.Extensions pins to 10.0.12 | Upstream's dependency bot may bump these first | At each sync and before `multidb/upstream`: if upstream is already at or past 10.0.12, drop step 23; if it moved some pins only, re-cut it against the new pins. The MySQL provider needs EF >= 10.0.12. |
| Our code routines | step 06 adds `20261012120000_RemoveNulFromItemKeys` | Upstream's latest routine at `61f9a9df23` is `20260915120000_MigrateRatingLevels` | Before `multidb/upstream` is built, every routine the series adds must sort after upstream's latest routine; if upstream adds a later one, re-date ours. Check how each interleaves with EF migrations on SQLite and that it is pre-marked on a fresh PostgreSQL install. |

  - Also re-run the scan's searches at each sync: `git grep -i -E "postgres|npgsql|mysql|mariadb"` on `upstream/master`, plus the commit log under `src/Jellyfin.Database`, `Jellyfin.Server.Implementations/Item`, `Jellyfin.Server/Migrations` and `FullSystemBackup` since the last sync.
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
