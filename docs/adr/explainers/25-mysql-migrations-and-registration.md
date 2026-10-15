# Explainer: step 25, the MySQL and MariaDB migrations and registration

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/25-mysql-migrations-and-registration`, 9 commits on the synced `feature/multi-db` tip `81f29427e6`, final tip `f8f81e4b48`.
  - `d2fba4eee4` Add the MySQL and MariaDB baseline migration
  - `8e409d02c4` Test the migrated MySQL and MariaDB schema on a server
  - `59033dc09b` Register the MySQL and MariaDB provider and warn that it is experimental
  - `399baa3049` Run the migration-dependent tests on MySQL and MariaDB
  - `05e885de1b` Detect the MySQL server version without the database
  - `e9cf496a10` Start the integration server on MySQL and MariaDB
  - `addf2cebad` Give the MySQL provider migrations the id of the SQLite migration
  - `f59b9432d5` Generate the MySQL migration with dev/add-migration
  - `f8f81e4b48` Check the MySQL and MariaDB migrations with the workflow tests
- **Size:** 41 files, +7068/-154. About 5,260 of those lines are the generated baseline, its designer file and the model snapshot. Production code, the command and the readme are about 300 lines; the rest is tests.
- **Status:** reviewed over several rounds (backups `multidb/25-backup-a` to `-g`, `multidb/25-v2`, `multidb/25-before-rebase-2c70`). The nine commits were approved, then rebased onto the upstream sync `81f29427e6` with `git range-diff` showing all nine identical. The pre-sync tip is kept on `multidb/25-before-sync-rebase` (`1f6dfa2e50`). After a definitive proof per commit and at the tip, the branch was merged into `feature/multi-db` with `--no-ff` as `abf45dcad2`. The merge tree equals the tip tree (`1f77b768`). The post-merge proof is green. This worker made no push; the coordinator later reported all fork PR #1 CI jobs green at the merge SHA (pr-plan B20).
- **Relevant decisions:** ADR-0001 W3, ADR-0011 tiers (Experimental means selectable, with a startup warning) and F1/F6, ADR-0003 S1 (the migration-in-progress marker), pr-plan 25 and B17.
- **With this step a server can run on MySQL or MariaDB.** A `database.xml` that names `Jellyfin-MySQL` selects the provider. A fresh database migrates in one baseline, and the integration suite starts the server on both engines.

## Why it exists

Step 24 built the provider but did not register it, and it had no migrations. The test suite ran against a schema built from the model, and every test that needs EF migrations was skipped on these servers. This step adds the baseline migration and the registration, so a server can start on MySQL or MariaDB. It then lets the skipped tests run, and it brings the provider into the migration workflow of step 22b (ADR-0001 W3), so the next contributor's database change reaches it with the same one command.

## What each commit does

- **The baseline migration.** This commit adds one `20200101000000_InitialCreate`, generated from the current model. It covers every SQLite migration up to `20261013120000_AddNormalisedMatchColumns`, the same coverage as the PostgreSQL baseline, so none of those is repeated for this provider.
  - **Design-time factory.** `MySqlDesignTimeJellyfinDbFactory` fixes the server version at `8.4-mysql` and never connects. So the files come out the same wherever they are generated.
  - **Baseline constants.** `MySqlMigrationBaseline` records the baseline id and the last SQLite migration it covers, as `PostgresMigrationBaseline` does.
  - **Offline tests.** They check that the model has no pending changes for the MySQL 8.4 and MariaDB 11.4.7 flavours, that the baseline sorts before every SQLite id, that the covered SQLite migration exists, and that later MySQL migrations pair with later SQLite ones.
  - **Fixture default.** The test fixtures now take their schema from the migrations on every provider. `TestDatabaseEnvironment.DefaultSchemaSource`, which picked the model for MySQL and MariaDB, is gone.
  - **Version names.** The test server and the provider's example name MariaDB as `11.4.7-mariadb`, the floor, rather than `11.4`.
- **The migrated schema on a real server.** On both servers the tests migrate an empty database and check four things:
  - every migration is applied, starting with the baseline, and none is pending;
  - the model has no pending changes;
  - tables, columns, indexes and foreign keys match a database built from the model (read from `information_schema`);
  - the prefix of every indexed column (`STATISTICS.SUB_PART`) matches the prefix the step 24 convention chose, in both directions. So a text column indexed in full, or an unexpected prefix, fails.
- **Registration and the warning.** `MySqlDatabaseProvider` joins the provider list in `ServiceCollectionExtensions`, and `Jellyfin.Server.Implementations` references the project.
  - **Where the warning is logged.** The provider logs one Warning that it is experimental and that the data should be backed up first. The provider logs it itself, so no public API changes.
  - **Why once per database.** The server builds two providers in one start: one for the migrations that run before startup, and one for the running server. A warning in the constructor or on every setup showed twice in a real start. So the provider remembers, for the life of the process, which databases it has warned about. They are keyed by server, port and database, never by password.
  - **Tests.** A single Warning; no second warning for the same database; another database does warn; the constructor logs nothing; a `database.xml` that selects the provider creates it. One test builds both of the server's containers and counts one warning.
- **The migration-dependent tests run.** `MigrationsFact`/`MigrationsTheory`, which skipped on "no migrations yet", are replaced by `MigrationFact(id)`. It skips a single-migration test only when the provider's baseline already includes that change, that is, when the id sorts at or before the baseline's coverage. That is true only for the four `AddNormalisedMatchColumns` tests. A migration that is missing for any other reason does not skip, so the test runs and fails rather than passing unnoticed. Tests cover a missing migration at, before and after the coverage on PostgreSQL, MySQL and MariaDB.
  - **Pool fingerprint.** A MySQL lease is reset by emptying the tables, which keeps the migration history. The pool's schema fingerprint now includes that history, so a test that records or removes a migration gets its database dropped instead of handed out again.
  - **Backup tests.** They write history rows through the provider's history repository instead of hand-quoted SQL.
  - **Partial failure.** A migration that fails partway on a real MySQL-family server leaves a marker that names it, writes no history row, and makes the next startup refuse.
  - **Transactional-provider tests.** The three tests for a provider with transactional migrations now run on every engine. Where the engine is not transactional, a wrapper that reports it as transactional stands in for it. These tests therefore test how the migration service treats such a provider, not the engine.
- **Server version without the database.** When `database.xml` does not name the server, the provider asks the server for its version. That connection used to select the configured database, so a dropped database failed with "could not be asked for its version". It never reached the check that explains the missing database, and a first start could not create it. The connection now clears `Database` and turns pooling off, since it is used once. A server that is absent, refuses the connection, or never answers already surfaces as the same option message, and a test covers the server that does not answer.
- **The integration server on MySQL and MariaDB.** `JellyfinApplicationFactory` creates its database through the selected provider's backend (`TestDatabaseEnvironment.Backend`), not only `PostgresTestBackend`, and writes that provider's `database.xml`. The tests that start over a lost database, and the factory tests, run on all three server engines. Losing every table on MySQL and MariaDB also removes the migration history, so the guard reports missing history there and an empty history on PostgreSQL.
- **Paired migration ids.** The provider registers design-time services with a `PairedMigrationsIdGenerator` that reuses the id of the SQLite migration of the same name, exactly as 22b does for PostgreSQL. It warns when it cannot find the SQLite migrations.
  - **Why a copy.** The generator is a copy, not a shared file. Both provider assemblies expose their internals to the same test project, so a type linked into both would exist twice there and fail to compile.
  - **Drift guard.** Two tests keep the copies equal. One runs the same cases through both generators and compares ids and warnings. The other compares the source of the two generators and the two design-time services, with provider names and comments removed, and names the first line that differs.
- **`dev/add-migration` generates the MySQL migration.** One table row in the command. The readme gains a table of providers, projects and baselines, and says MySQL and MariaDB share one set of migrations generated for MySQL 8.4, so a change is tested on both.
- **The workflow theory covers MySQL and MariaDB.** `MigrationProvider` gains rows for MySQL 8.4 and MariaDB 11.4.7, the oldest supported version of each, because the snapshot depends on the server version. The provider has no upper version limit and builds its model for the server it finds, so rows for MySQL 9.7, MariaDB 11.8 and MariaDB 12.3 check that a newer server does not change the model. None has pending changes.

## What the review found and what was done

The review notes themselves are not kept with the evidence. What the rounds changed can be read from the scratch directories and the commit history:

- **The second server start showed the warning twice.** That led to the once-per-database rule. Against a mutated provider that warned on every setup, the two once-only tests failed (`/tmp/step25/s3/reg-mutant.log`, 2 failures; `reg-guard.log`, none).
- **Early MySQL and MariaDB integration runs failed with "could not be asked for its version"** (`/tmp/step25/c4/early-mysql-integ.log`). That is the reason for the version-detection commit. A probe showed that a refused connection, a timeout and an unreadable reply all surface as a `MySqlException` inside the provider's `InvalidOperationException` (`/tmp/step25/s4-probe.txt`), which is what the commit body claims.
- **Newer server versions.** The offline rows for MySQL 9.7, MariaDB 11.8 and 12.3 were added in a fold-in (`/tmp/step25/m2/rows.diff`).
- **The skip rule was tightened.** An earlier wording skipped a test whenever the provider's own migration list lacked the id. The final rule skips only ids at or before the baseline's coverage, so a genuinely missing migration fails. The fixture default moved into the baseline commit, where it belongs.
- **Follow-ups the gate recorded, not done in this step:**
  - the generator copy plus its parity test is accepted for now. The long-term fix is one shared design-time generator and SQLite-directory finder, kept internal in `Jellyfin.Database.Implementations` (ABI-checked). It is decided in the 22c design and must land before SQL Server adds a third copy;
  - in the upstream replay, the version-detection commit becomes a fixup into step 24's commits;
  - step 27a: put "include the history in the fingerprint" into the pool contract, and move the provider-to-assembly switch out of `ProviderRequirement.MigrationIds`;
  - move `OfflineContexts` out of the PostgreSQL test namespace;
  - the first MySQL migration after the baseline needs an upgrade test from a baseline-only database;
  - document that the transactional wrapper tests service behaviour only;
  - step 26 adds MariaDB 11.8, the newest LTS, as an advisory CI entry.

## The proof

- **Attempt 1** (`/tmp/step25/definitive.YEpH2awT`) stopped with rc 23 at the first commit's SQLite Implementations validation. The suite itself passed with 0 failures. The validator assumed one TRX result per test definition, but an xUnit theory with non-serialisable data has one definition with several results and repeated display names. The validator was fixed, and ambiguous names now fail closed for gated lookups; its self-test passes 20/20. Before that, a launch had been refused with rc 89 because a compiler server from an earlier build still held the heavy-operation lock (see the proof lessons in `pr-plan.md`).
- **Run 2** (`/tmp/step25/definitive.V40kdWS7`) used the re-reviewed validators. Every per-commit leg passed for all nine commits: Release build with 0 warnings, `dotnet format`, SQLite x3, and PostgreSQL, MySQL and MariaDB Implementations and Server.Tests. From `e9cf496a10` on, integration also ran on all three server engines.
  - **At the tip:** Release 0/0; format; SQLite x3; PostgreSQL Implementations and Server.Tests twice; PostgreSQL, MySQL and MariaDB x3; MariaDB 11.8 advisory 2346/137, reporting 11.8.9.
  - **Debug warnings:** 211 unique, identical to the exact sync-base warning set. The separate comparison against the older historical baseline showed only the informational `SessionManager` CA1721 line shift.
  - **Reconciliation:** the unskip checklist holds 35 methods, the tests step 24 had to skip for lack of migrations. On each engine 31 pass and the 4 baseline-covered ones skip. That is 36 cases: 32 passed and 4 skipped.
  - **Workflow:** `dev/add-migration` on a temporary model change generated 6 files, and the targeted workflow tests passed.
  - **Abort at the end.** The run then ended with rc 59 at the container gate. It was adjudicated as a false positive caused by two unrelated containers from other projects on the shared machine. The surviving Compose container was directly inspected and had no Testcontainers labels. The other container had already been removed externally, so its labels could not be inspected; its external ownership was inferred from its project-specific name, image, the Jellyfin test configuration and the coordinator's ownership information. The ending HEAD and clean status were checked afterwards (`postrun-evidence/adjudication.txt`), and an ambiguity check over 113 TRX files found 0 repeated names in gated methods. The gate now counts only new Testcontainers-labelled identities.
- **Post-merge proof** at `abf45dcad2` (`/tmp/step25/postmerge`): 14 gates rc 0 plus the complete marker. Release 0/0. Every Implementations run passes `ProviderBackend_IsLive`. The only container left at the final snapshot was this run's own Ryuk, which then reaped itself, leaving 0 Testcontainers containers.

## Counts (passed/skipped, 0 failures)

At the merge `abf45dcad2`:

| Engine | Implementations | Server.Tests | Integration |
|---|---|---|---|
| SQLite | 2386/111 | 152/9 | 137/11 |
| PostgreSQL 16.15 | 2418/76 | 146/15 | 144/6 |
| MySQL 8.4.11 | 2346/137 | 126/35 | 144/6 |
| MariaDB 11.4.13 | 2346/137 | 126/35 | 144/6 |

Before the step, at the sync tip `81f29427e6`: MySQL and MariaDB 2251/147 and 104/55, with no integration suite. Server.Tests on the MySQL family rose from 104 to 126 passed, and the integration suite runs there for the first time.

**Server.Tests provenance is companion-based.** Server.Tests has no liveness sentinel of its own. Its provider runs are attributed to their engine by two things: the Implementations sentinel of the same run, with the same provider and expected-provider variables, and the audited provider selection in `Jellyfin.Database.Testing`. A standalone sentinel in Server.Tests is the new 22d item (pr-plan B21).

## Limits

- The MySQL family stays Experimental and fork-only (ADR-0011): selectable, with a startup warning.
- The 22c contents check does not exist yet, so the provider's rows in it come with 22c.
- There is no upgrade test yet from a baseline-only database to a later MySQL migration, because there is no later MySQL migration yet.
- CI has no MySQL or MariaDB entry until step 26.

## Things worth knowing when defending it

- **Why one migration set for two servers?** One provider serves both (ADR-0011). The model snapshot depends on the server version, which is why the theory checks the oldest version of each, and newer ones, against the one snapshot.
- **Why does the baseline cover through `AddNormalisedMatchColumns`?** It matches the PostgreSQL baseline, so every later SQLite migration needs a paired migration on both server providers, and the pairing theory checks that.
- **Why skip any migration test at all?** A test of `AddNormalisedMatchColumns` migrates from the schema before that change. The MySQL family never had that schema, because its baseline already includes the change. Any other missing migration is a real gap and fails.
- **The warning cache lives for the process.** Two providers in one start is how the server is built today. A change there would still warn once per database, not once per provider.
