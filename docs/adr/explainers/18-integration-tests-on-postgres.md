# Explainer: step 18, integration tests on PostgreSQL

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/18-integration-tests-on-postgres`, two commits on the `feature/multi-db` tip after step 16 (`337ea92882`).
  - `c8978dbcce` Run the integration test server on the selected database provider
  - `1a13594046` Cover a server that lost its PostgreSQL database in StartOver tests
- **Status:** gated, gate findings fixed, rebased onto step 16 without conflicts or code changes (`git range-diff` shows both commits identical), and merged into `feature/multi-db`. Not pushed. Backups: `multidb/18-backup-a`, `-b` (before the fixes), `-c` (`ad6c1177fb`, before the rebase).

## Why it exists

The integration tests start a real server through `JellyfinApplicationFactory`. The factory never wrote a database configuration, and a server without `database.xml` writes a SQLite one. So `JELLYFIN_TEST_DB_PROVIDER=Postgres` changed nothing for these tests: they ran on SQLite every time, and passed, which looked like PostgreSQL coverage and was not.

## What changes, commit by commit

- **The factory provisions the selected provider.** With PostgreSQL selected, the factory creates an empty database through the test backend (`PostgresTestBackend.CreateEmpty`) and writes the `database.xml` that points the server at it before the host starts. The schema is then built by the server's own migrations, as on a real install. Nothing changes with SQLite.
  - The database is dropped on dispose, also when disposing the host throws. The drop forces closed any connection a host that failed to stop left open.
  - `database.xml` holds the password. It is written with mode 0600 on Unix and deleted on dispose in a `finally`. The test-host directory is deleted only if the factory created it.
  - A factory started again on a directory that already holds a `database.xml` keeps that database and does not own it. A `database.xml` naming another provider than the selected one is refused with `InvalidOperationException`, so a reused directory cannot quietly run on SQLite while PostgreSQL is selected.
  - The connection string goes only into `database.xml`, never into test output.
  - `JellyfinApplicationFactoryTests` pin all of this; the two old start-over tests delete SQLite files, so they are marked SQLite-only.
- **StartOver on PostgreSQL.** A PostgreSQL server can lose its database in two ways: dropped, or present but empty. For each, two cases: with the wizard reset, the server starts as new and applies every migration; with the wizard still completed, the server refuses to start and says why. After a refusal the system configuration and `database.xml` are unchanged and the refusal created no database and no table. Four PostgreSQL cases in total. These tests create the database and write `database.xml` themselves, so the factory leaves it alone.

## Evidence

- Both commits build alone in Release with 0 warnings; `git diff --check` is clean; Debug warnings at the tip match the base.
- **Red before green.** On the parent, four factory tests were red on PostgreSQL: the server had run on SQLite. The four StartOver PostgreSQL cases are new tests and were green on their first run, so they were never seen red. That they would catch a regression rests on what they assert, not on a recorded failure.
- After the rebase onto step 16:
  - PostgreSQL integration, twice: 139 passed, 6 skipped, 0 failed. The skips are three upstream `UserLibraryControllerTests` ("Disabled for flaky execution after refactor", upstream `e4b11c664c7`) and three SQLite-only tests. The provider-proof tests and all four StartOver cases passed both times.
  - SQLite: Implementations 1553 passed, 84 skipped; Server 85 passed, 7 skipped; Integration 132 passed, 11 skipped.
  - PostgreSQL Implementations 1584 passed, 50 skipped; Server 92 passed.
  - No `database.xml` naming PostgreSQL was left behind by these runs.
- `RenameLibrary` ran 30 times in a row on PostgreSQL without a unique violation (23505). That is evidence, not proof: the earlier write-conflict retry and its classifier make a pass plausible, but a race that did not happen in 30 runs can still happen.

## Gate findings that changed the code

- The first version left `database.xml`, with the test password, in every PostgreSQL test-host directory under `/tmp/jellyfin-test-data`. The fix is the 0600 write and the delete on dispose described above. The leftovers on this machine (172 removed by the reviewer and about 300 more removed afterwards) were local only: GitHub runners are thrown away after each job.

## Limits

- Integration tests run on SQLite and PostgreSQL only. MySQL, MariaDB and SQL Server join when their providers and test backends exist.
- This step adds a reference from the integration project to `Jellyfin.Database.Testing`. The CI database job (step 17) runs every test project that references the harness, so the integration tests join it without a workflow change. Their run time on a GitHub runner is not yet measured.
