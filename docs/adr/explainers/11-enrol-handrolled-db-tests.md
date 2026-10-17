# Explainer: step 11, hand-rolled database tests enrolled in the fixture

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/11-enrol-handrolled-db-tests`, seven commits on the `feature/multi-db` tip after step 17 (`aec1028f44`).
  - `ec213ce7a9` Run the UserManager tests on the selected database provider
  - `31fedf5de2` Run the user data and API key tests on the selected provider
  - `35803638c0` Run the item count tests on the selected database provider
  - `0e845f4949` Run the migration routine tests on the selected provider
  - `80a0eaca8d` Run the backup service tests on the selected provider
  - `c997b798b7` Open SQLite through the test fixture in the SQLite-specific tests
  - `a040f41576` Ban opening SQLite directly in test projects
- **Status:** gated, PASS WITH NITS (no blockers). Four nits and one optional item were folded in during the rebase onto step 17. Merged into `feature/multi-db`. Not pushed. Backup: `multidb/11-backup-a` (`a94eaa0dd9`, the gated version before the rebase).

## Read this first: why PostgreSQL shows 13 fewer passing tests

Comparing PostgreSQL runs before and after this step, 13 tests move from passed to skipped. That is a correction, not a regression.

- **The 13.** These tests opened their own SQLite database, whatever provider was selected. Under the PostgreSQL setting they "passed" on SQLite. They now go through the fixture, and are marked SQLite-only because what they test cannot exist on PostgreSQL:
  - 11 `RemoveNulFromItemKeys` tests seed NUL characters, which PostgreSQL `text` rejects. The routine exists to clean up data only SQLite can hold.
  - 1 `ConsolidateLocalizedUserViews` test seeds an orphan row. PostgreSQL always enforces foreign keys, and faking it would need superuser rights.
  - 1 backup test plants a malformed `KeyframeTicks` JSON value. On PostgreSQL that column is `bigint[]` and cannot hold malformed JSON.
- **The 117.** These tests used to run on SQLite even with PostgreSQL selected. They now really run on PostgreSQL:
  - 99 in Implementations: Backup 35, ItemCount 23, NormalizedUsername 25, UpdateUser 4, UserData 8, Auth 2, ProfileImage 2.
  - 18 in Server, from the migration routines.
- Totals are unchanged against the step 17 merge. The only moves are those 13, from passed to skipped. The reviewer confirmed this from a TRX test-name diff on both providers: nothing was dropped or renamed, and SQLite results are identical.

## Why it exists

The harness (step 10 onwards) lets a test say "give me a database for the selected provider". About a dozen older test classes predated it and built their own SQLite connection, so they never ran on anything else, and the Server.Tests project had no PostgreSQL coverage at all. That gap was invisible, because those tests still went green under `JELLYFIN_TEST_DB_PROVIDER=Postgres`.

## What changes

- **Moved to every provider:** the three UserManager classes, UserDataManager, AuthenticationManager (API keys), ItemCountService, ApplyDatabaseSettings, the backup service tests, and the `HarmonizeConflictingUserData`, `RepairAlternateVersionLinks` and `MigrateLinkedChildrenStaleItems` routines. The routines use EF only, so they can run anywhere, which the review confirmed in `Jellyfin.Server/Migrations`.
- **Through the fixture, but SQLite-only with a stated reason:**
  - the 13 above;
  - the SQLite purge, write-conflict classification and LIKE parity tests, which test SQLite itself.
  - `ItemPersistenceWriteConflictTests` needs a second connection to a file database, so it keeps narrow, statement-level suppressions.
- **Backup restore tests on PostgreSQL** now hand the purge, identity insert and restore completion to the provider under test. The rollback test asserts the provider's own unique violation: SQLite extended code 2067, PostgreSQL SQLSTATE `23505`. This is stricter than before: the test used to accept any `SqliteException`, and an intermediate version that accepted any `DbException` was caught in review.
- **`CreateEmpty()` on the backend contract** returns a database with no schema, for the test of a database that was only seeded. It is recorded in ADR-0002 H1: on the pooled engines, it will be a fresh database, never a leased one.
- **The ban.** `tests/BannedSymbols.Tests.txt` is wired through `tests/Directory.Build.props` into every test project except `Jellyfin.Database.Testing`. It bans:
  - both `SqliteConnection` constructors;
  - `SqliteFactory.CreateConnection`;
  - the whole `SqliteDbContextOptionsBuilderExtensions` type, so every current and future `UseSqlite` overload;
  - `SqliteTestBackend.Instance`, so a plain `[Fact]` cannot quietly take SQLite again.

  The one legitimate use of `SqliteTestBackend.Instance` is the PostgreSQL restore test, which needs a SQLite archive as its source; it has a statement-level suppression with the reason. A blanket RS0030 suppression in `AlternateVersionQueryTranslationTests` was narrowed to the lines it was written for, because it would also have hidden the new ban. `SqliteConnection.ClearAllPools` stays allowed.
- The write-counting interceptor also counts scalar commands.

## Evidence

- Every commit builds in Release with 0 warnings (checked with `rebase -x`), and `git diff --check` is clean. Debug warnings at the tip match the base.
- The ban fires: a temporary probe raised RS0030 for every `UseSqlite` form, both constructors, the factory and `Instance`, in both Debug and Release. Ten entries in the first version each fired; the type-level entry replaced eight of them.
- SQLite: Implementations 1577 passed, 85 skipped; Server 85 passed, 7 skipped; Integration 132 passed, 11 skipped. These equal the step 17 tip.
- PostgreSQL, in container mode with `postgres.runsettings` and the expected provider set:
  - Implementations: 1608 passed, 51 skipped, twice.
  - Server: 80 passed, 12 skipped, twice.
  - Integration: 139 passed, 6 skipped.
- No product bug was found. Every converted test passed on PostgreSQL once its test assumptions were made provider-neutral, without loosening an assertion.

## Rebase note

There were no textual conflicts with step 17. One semantic conflict: the stub backend in step 17's `BackendLivenessTests` implements `ITestDatabaseBackend`, so it gained `CreateEmpty`, which throws `NotSupportedException` because the stub never creates databases.

## Limits

- The ban covers the ways the review found to open SQLite. A determined test could still reach it through reflection, or through a `DbProviderFactory` looked up by name. The ban guards against accidents, not intent.
- With step 11, the PostgreSQL track is complete. MySQL, MariaDB and SQL Server inherit all of this: every enrolled test runs on them as soon as their backends exist.
