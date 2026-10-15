# Explainer: step 26, MySQL and MariaDB in CI, and the operator docs

These are fork-internal study notes, not upstream PR text. They describe the branch and the retained proof as of 2026-10-15.

- **Branch:** `multidb/26-ci-mysql-mariadb`, 4 commits on `e5a50c3707`, final tip `adf55de42f`. The orchestrator fast-forwarded `feature/multi-db` to it.

  | Commit | Subject |
  |---|---|
  | `f0f4aa5f44` | Require each database test project's sentinel in its own results |
  | `25d552ff3e` | Run the database tests on MySQL and MariaDB in CI, without requiring them |
  | `08af2f5ce1` | Add how-to pages for running Jellyfin on PostgreSQL, MySQL and MariaDB |
  | `adf55de42f` | Add Docker Compose examples for Jellyfin on PostgreSQL, MySQL and MariaDB |

- **Backups, kept:** `-backup-a` `55e5374554`, `-backup-b` `2e8d1f7b39`, `-backup-c` `6afca304cb`, `-backup-d` `6bfef4ec33`.
- **Relevant decisions:** ADR-0011 (tiers and CI images; amendment F8), ADR-0008 R3, pr-plan row 26, B28 follow-up 6, and B30.
- **Evidence:** `/tmp/26/`.

## Why it exists

- **CI.** The MySQL family had no CI job. Under ADR-0011, an Experimental engine's job runs on every pull request without being required.
- **The sentinel check** read the results of all projects at once. A sentinel passing in one project could hide another project whose sentinel never ran (22d follow-up 6).
- **Operator docs.** There was no operator documentation for running Jellyfin on PostgreSQL, MySQL or MariaDB.

## What each commit does

### Per-project sentinels (`f0f4aa5f44`)

`ci-tests-db.yml` now reads each project's own TRX file and requires that project's sentinel there, by its full name:

| Project | Sentinel |
|---|---|
| `Jellyfin.Server.Implementations.Tests` | `ProviderBackend_IsLive` |
| `Jellyfin.Server.Tests` | `ProviderBackend_IsLive` |
| `Jellyfin.Server.Integration.Tests` | `CreateClient_SelectedProvider_RunsTheServerOnThatProvider` |

- **Provider and version.** The two `ProviderBackend_IsLive` projects must also report the expected provider and version. The version is escaped in the regular expression.
- **No project left out.** The sentinel table must equal the set of projects that reference `Jellyfin.Database.Testing`, in both directions. A new database test project fails the job until its sentinel is listed.
- **Required tests.** `requiredTests` entries name their project as `project:test`. The PostgreSQL entries keep their `pg_dump` restore test that way.
- **What stays the same.** The PostgreSQL job's name, its triggers and the required check do not change. The nightly workflow is updated to the new form.

### The MySQL-family workflow (`25d552ff3e`)

`ci-tests-db-mysql.yml` calls the database job for four entries, each image pinned by digest. None is a required check, and their names match no required check.

| Entry | Image | Role |
|---|---|---|
| `MySql` | `mysql:8.4.11` | oldest supported, the harness image |
| `MariaDb` | `mariadb:11.4.13` | oldest supported, the harness image |
| `MySql LTS newest` | `mysql:9.7.2` | advisory: its failure does not fail the run |
| `MariaDb LTS newest` | `mariadb:12.3.3` | advisory |

**Why these newest versions.** The workflow and the commit cite the vendors' release policies:
- MySQL 9.7.x is the newest LTS line, and 26.7 is an Innovation release, which is not tested.
- MariaDB 12.3 is a long-term release, maintained until June 2029.

**Triggers.**
- **Every `pull_request`.** A paths filter would miss what matters most here, such as shared LINQ that only MySQL or MariaDB cannot translate. This matches ADR-0011.
- **Nightly and `workflow_dispatch`.** These fire only once the workflow is on the default branch.

**Two new matrix options.** An entry can be advisory. An entry's expected version can be a major and minor, which tells one release series from another.

### The how-to pages (`08af2f5ce1`)

`docs/database/` has a README and one page per engine: `postgresql.md`, `mysql.md` and `mariadb.md`.

**What each page covers:**
- creating the database and its user;
- `database.xml`, with the provider name and every option the provider reads;
- the connection string from `JELLYFIN_DATABASE__CONNECTIONSTRING`;
- the first start, and the log line that names the provider;
- dumps, and Jellyfin's backup archive;
- upgrades of Jellyfin and of the server.

**One template.** The pages are rendered from `docs/database/templates/how-to.md` by `docs/database/render`, a .NET file-based app like `dev/add-migration`. `render --check` fails when a page is stale.

**Facts the pages state.**
- **Minimum versions.** These are the ones the code enforces: MySQL 8.4 and MariaDB 11.4.7 are refused below at startup, and PostgreSQL 16 is what the provider generates SQL for.
- **Collations.** `utf8mb4_0900_bin` on the MySQL family and `"C"` on PostgreSQL, as the provider sets per column.
- **Backups.** Home-screen sections are restored from X10 on.
- **Warnings.** MySQL and MariaDB carry the experimental warning.

**Known issues on the pages:**
- **Collation.** The database default collation is the server's default for utf8mb4. One column and `__EFMigrationsHistory` take it (B30).
- **Error 1020 on MariaDB 11.8 and later** (B30). Until 27a, 11.4 is the tested baseline, and 11.8 and 12.3 are advisory.

### The Compose examples (`adf55de42f`)

Under `docs/database/compose/` there is one example per engine, `postgresql/`, `mysql/` and `mariadb/`, each with an `env.example`. They use the harness images: `postgres:16.15-trixie`, `mysql:8.4.11` and `mariadb:11.4.13`.

- **The Jellyfin image.** Upstream images have no provider for these engines, so `compose/Dockerfile` builds the server from the checkout. It lays the build over an official unstable image, pinned by digest, for the web client and FFmpeg.
- **The connection string** comes from `JELLYFIN_DATABASE__CONNECTIONSTRING`, with the password from `.env`, which is ignored by git.
  - The variable reaches the configuration through `AddEnvironmentVariables("JELLYFIN_")` (`Program.cs:416`).
  - The override is applied to the provider's copy of the configuration in `ServiceCollectionExtensions.cs:152-165`.
  - So the string is in neither `database.xml` nor a backup archive.
- **`database.xml`** is inline in the Compose file. The image copies it into the configuration volume on the first start only, because a restore rewrites that file and a read-only mount would break it.
- **Health checks gate the order.** Jellyfin waits for the database over TCP, and counts as healthy once `/health` says `Healthy`.
- **Rendering.** The Compose files and `env.example` are rendered by the same `render`.

## Proofs

**Tip proof** (`/tmp/26/proof/`, `COMPLETE`, 14/14 gates rc 0, Release 0/0, format 0).
- **Where it ran.** At `6afca304cb` (backup-c). Every change from there to `adf55de42f` touches only `.github/workflows/` and `docs/database/` (`git diff --name-only 6afca304cb adf55de42f`).

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2462/112 | 157/9 | 137/11 |
| PostgreSQL 16.15 | 2496/74 | 147/18 | 144/6 |
| MySQL 8.4.11 | 2371/157 | 127/38 | 144/6 |
| MariaDB 11.4.13 | 2371/157 | 127/38 | 144/6 |

- **TRX rows.** 12 TRX files, 11,459 rows: 10,827 passed, 632 skipped, none failed (I recounted them).
- **Sentinels.** All pass on every engine.
- **Identity.** Start and end identities are exact, with empty status files.
- **Docker.** The added and remaining-new lists are empty. The removed list has one pre-existing Testcontainers Ryuk container.

**Debug** (`/tmp/26/debug/`). 211 against 211, empty diff, `e5a50c3707` against `6afca304cb`.

**Per-commit gates.** Each gate checks Release 0/0, the exact format command, a workflow lint, and `render --check` where `render` exists.
- **Round 3** (`/tmp/26/round3/chain/results.tsv`) gated `f0f4aa5f44`, `25d552ff3e` and the backup-d pair.
- **Round 4** (`/tmp/26/round4/chain/results.tsv`) gated the folded `08af2f5ce1` and `adf55de42f`.

**The CI jobs, run locally** (`/tmp/26/ci-legs/`, `/tmp/26/round3/ci-legs/`). Each job's own steps were run on this machine:

| Job | Server | Result |
|---|---|---|
| PostgreSQL | 16.15 | success |
| MySql | 8.4.11 | success |
| MariaDb | 11.4.13 | success |
| MariaDb, newest at the time | 11.8.9 | success |
| MySql LTS newest | 9.7.2 | success |
| MariaDb LTS newest (advisory) | 12.3.3 | 1 integration failure, error 1020 |

A separate earlier run on MariaDB 11.8.9 (`/tmp/26/advisory-mariadb-11.8/`) also hit error 1020 once, in the same integration test (`RenameLibrary_UserHasLibraryPreferences_RemapsLibraryId`). So 1020 on 11.8 is intermittent, not absent.

**Negative control** (`MySql-negative`, and `/tmp/26/check-negatives/`). With the Server sentinel filtered out:
- the new check fails;
- the old check at `e5a50c3707` passes.

**Check-step controls: 12 of 12 as expected.** The positive cases pass for PostgreSQL, MySQL and MariaDB. Each of these fails:
- a nightly entry on 16;
- a missing integration TRX;
- a skipped Implementations sentinel;
- a project without a sentinel;
- an unknown project in `requiredTests`;
- a bare method name in `requiredTests`;
- an 11.8 entry run on 11.4.

**Compose** (`/tmp/26/compose/`, at `6afca304cb`; the Compose files did not change after it). Six bring-ups: each engine, with the connection string from the environment and from `database.xml`.
- Each became Healthy and logged its provider.
- Each did a dump and restore round trip with the same table count.
- Teardown left no containers, volumes or networks.
- **One script bug.** The script's own final summary used `\t` in `grep -E` and so exited 1. `VERDICT-recomputed.txt` recomputes it with `awk`: 0 of 7 rows failed.

## Review

Relayed by the coordinator; the review notes are not in the evidence.
- **First reviewer.** Approved the CI, with a should-fix: 11.8 was mislabelled as the newest LTS.
- **Split checks.** The orchestrator checked the Compose examples, and the coordinator checked the collation text. Several reviewers dropped on API connection errors, which is why the review was split.
- **Fold rounds:**
  1. every-PR triggers;
  2. the advisory entries moved to 9.7.2 and 12.3.3;
  3. the collation wording;
  4. the 1020 known issue.

## Findings for 27a (B30)

**1. Isolation (confirmed bug).**
- **How it happens.** MySqlConnector 2.6.2, which Microting 10.0.12 depends on, maps `IsolationLevel.Unspecified` to REPEATABLE READ. It then sends `set session transaction isolation level repeatable read` ahead of `start transaction`. The copy of `MySqlConnection.cs` in `/tmp/26/isolation/src` shows this at lines 195-205 and 239-250.
- **Effect.** Every `BeginTransaction()` without a level overrides the READ COMMITTED that `SessionSettingsInterceptor.cs:30` sets. It holds for the transaction, and for the rest of the connection.
- **Confirmed** by a runtime scratch check (`/tmp/26/isolation/`) on MySQL 8.4.11 and 9.7.2 and MariaDB 11.4.13, 11.8.9 and 12.3.3:
  - inside such a transaction, `@@transaction_isolation` is REPEATABLE-READ;
  - a row committed by another connection is not seen;
  - after the transaction the session stays at REPEATABLE-READ.
- **The fix shape.** With an explicit `ReadCommitted`, all five servers stay at READ COMMITTED.

This violates ADR-0008 R3 on both servers. It is 27a's first item, and 27a records the ADR-0008 correction.

**2. Error 1020 (`ER_CHECKREAD`) on MariaDB 11.8 and 12.3.**
- `innodb_snapshot_isolation` is on by default there: 11.4.13 is 0, 11.8.9 is 1 and 12.3.3 is 1.
- Under the REPEATABLE READ of finding 1, an update of a row changed since the transaction's read fails with 1020. The scratch check reproduces it on 11.8.9 and 12.3.3.
- The classifier at `MySqlDatabaseProvider.cs:183` retries only duplicate key, deadlock and lock wait timeout, not 1020.

**3. Collation.**
- **One column.** In the MySQL baseline, `KeyframeData.KeyframeTicks` (`20200101000000_InitialCreate.cs:525`) is the only one of 102 text columns without `utf8mb4_0900_bin`. It is not indexed or keyed.
- **The database default.** `AlterDatabase` at `:15` sets only the character set, so the database default is the server's case-insensitive default. `__EFMigrationsHistory` inherits it, harmlessly.
- **Fix in 27a:**
  - the convention covers converted and primitive-collection columns;
  - `AlterDatabase` sets the collation;
  - the baseline is regenerated;
  - a test checks every text column.

## Earlier CI, relayed

CI at the X10 commit `939887782c` was all green, including `ABI - Difference`, which is consistent with an additive `DbSet`. PostgreSQL is CI's only database job.
