# ADR-0010: CI gating for database provider tests

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- Upstream CI is `.github/workflows/ci-tests.yml`. It runs `dotnet test Jellyfin.sln --configuration Release` with coverage on an `ubuntu-latest` / `macos-latest` / `windows-latest` matrix (`fail-fast: false`).
- Only the ubuntu runners reliably support Linux containers. macOS runners have no Docker, and Windows runners run Windows containers.
- ADR-0002 makes Postgres opt-in through `JELLYFIN_TEST_DB_PROVIDER=Postgres`. Once selected, Postgres is required: an unavailable container makes tests fail, not skip. `[PostgresFact]` tests skip with a reason when Postgres is not selected.
- Constraints:
  - Runners without Docker must not break.
  - Where Docker exists, Postgres coverage must not be skipped silently.
  - Upstream PRs should change `ci-tests.yml` as little as possible.

## Options considered

1. Auto-detect Docker in the matrix job and run Postgres where it is available. This depends on the OS: Windows has Docker but not Linux containers. It is also non-deterministic, and a broken daemon turns silently into skips.
2. A trait filter in the matrix job (`Provider!=Postgres`) plus a Postgres job (`Provider=Postgres`). Every cross-provider test would need a trait, which ADR-0002 avoids, and the matrix command line changes.
3. **Leave the matrix job alone, because its default is SQLite. Add one ubuntu-only job that selects Postgres explicitly.**
4. A GitHub `services: postgres` container instead of Testcontainers. CI would then differ from local runs, and per-test databases and templates would still need the same backend code.

## Decision

**Option 3.**

**Existing matrix job:** no change. With no environment variable set it runs SQLite on all three operating systems. Postgres-only tests show as *skipped* with the reason "Postgres not selected (set JELLYFIN_TEST_DB_PROVIDER=Postgres)". No runner needs Docker.

**New job `run-tests-postgres`** in `ci-tests.yml`:

- Runs on `ubuntu-latest`, with the same triggers as the matrix and `timeout-minutes: 30`.
- `env`: `JELLYFIN_TEST_DB_PROVIDER: Postgres` and `JELLYFIN_TEST_POSTGRES_IMAGE: postgres:16-trixie`.
- Step `docker pull $JELLYFIN_TEST_POSTGRES_IMAGE`, so a registry or network failure shows as an infrastructure failure, not as test failures.
- Runs `dotnet test <project> --configuration Release --logger "trx;LogFileName=pg.trx"` for **every test project that references `Jellyfin.Database.Testing`**. A shell loop finds them with `grep -l Jellyfin.Database.Testing tests/*/*.csproj`, so the list maintains itself. If the loop finds no projects, the step fails.
- **Fail-closed guards:**
  1. Selection means requirement (ADR-0002). Container or connection errors fail the tests.
  2. An unknown provider value throws.
  3. The sentinel test `[PostgresFact] PostgresBackend_IsLive` checks `SELECT current_setting('server_version_num')` ≥ 160000. A final step greps the TRX and fails unless the sentinel's outcome is `Passed`, so a misconfigured job cannot turn into an all-skip green.
- Known gaps (ADR-0002) report as skipped, with their `ADR-xxxx/Gn` IDs in the reason. They are visible but do not fail the job. A known gap that starts passing fails the job.

**Gating policy:**

- In the fork, `run-tests-postgres` is a **required** status check on the integration branch as soon as it exists.
- Upstream, the job arrives in the CI PR after the provider and migrations PRs (`pr-plan.md`). It is required from the start: the ledger turns expected failures into skips, so the job is green on arrival.

**Later:**

- A scheduled (nightly) copy of the job runs with `JELLYFIN_TEST_POSTGRES_IMAGE=postgres:18` to catch problems on newer servers. It is not required.
- MySQL and SQL Server get sibling jobs: `JELLYFIN_TEST_DB_PROVIDER=MySql` or `SqlServer`, the same loop and the same sentinel pattern.

## Consequences

- The upstream matrix job keeps its exact command line. Only a new job is added, which keeps the CI PR small and easy to review.
- On the ubuntu matrix leg, Postgres-only tests are skipped even though Docker is present. That is by design: the dedicated job owns Postgres coverage, and its sentinel check proves it ran.
- The Postgres job duplicates the non-DB tests inside the DB-bearing projects. They are cheap, and duplicating them is simpler than maintaining trait filters.
- Coverage from the Postgres job is not merged into the matrix report at first.
- Locally, `dotnet test` behaves as before. `dotnet test --settings tests/postgres.runsettings` reproduces the CI job.

## Amendments (multi-target re-check, 2026-10-12)

- **CI1. One matrix job, `run-tests-db`, replaces the single `run-tests-postgres` job.**
  - `strategy.matrix.include` holds one entry per server engine, each with a provider name, a pinned image and a required flag.
  - The entries:
    - `Postgres` (Supported, required);
    - `MySql`, `MariaDb` and `SqlServer` (Experimental, not required while Experimental; see ADR-0011).
  - `fail-fast: false`. Each entry runs the same project loop with a TRX file per provider. `JELLYFIN_TEST_DB_PROVIDER` and `JELLYFIN_TEST_<ENGINE>_IMAGE` come from the matrix entry.
  - Promoting a provider to Supported flips its required flag. That is a repository setting, not code.
- **CI2. One generic sentinel.**
  - `[ProviderFact(Postgres, MySql, MariaDb, SqlServer)] DatabaseBackend_IsLive` asserts that the selected backend's server version is at or above the floor: PostgreSQL 16, MySQL 8.4, MariaDB 11.4, SQL Server 2022.
  - The TRX step checks that this test passed, in every job.
- **CI3. SQL Server's entry:**
  - it needs amd64 (`ubuntu-latest` is amd64) and at least 2 GB of RAM;
  - it sets `ACCEPT_EULA=Y` and `MSSQL_PID=Developer`;
  - the image pull stays a separate step, so registry failures show as infrastructure failures;
  - startup is slower, so its timeout is 40 minutes.
- **CI4. How it arrives upstream.**
  - PR 17 in `pr-plan.md` introduces the matrix with only the Postgres entry.
  - The last PR of each engine group adds that engine's entry (PRs 26 and 29).
  - The nightly job becomes a matrix of each engine's newest LTS or minor release.
- **CI5. Pinning.** Images are pinned by tag in this ADR and in ADR-0011, and by digest in the workflow. Renovate updates both.

## Amendments (harness final gate, 2026-10-12)

- **CI6. The sentinel enforces the version floor.** The harness's first version only checked that the server version was not blank. That was the defect the final gate found.
  - Each server backend reports its parsed server version.
  - The sentinel asserts that version is at or above the provider's floor, from one table in the test library: PostgreSQL 16.
  - The other floors (MySQL 8.4, MariaDB 11.4, SQL Server 2022) are added with each provider's backend.
  - The sentinel keeps its shipped name, `ProviderBackend_IsLive`, which supersedes `DatabaseBackend_IsLive` and `PostgresBackend_IsLive`.
- **CI7. The default image is the pinned tag from ADR-0011**: `postgres:16.15-trixie`, not the floating `postgres:16-trixie`. This supersedes the `env` example in the Decision. The workflow pins by digest (CI5).
- **CI8. Skip reasons use the code's wording.**
  - A provider-restricted test: "Requires provider(s) Postgres".
  - A known gap: `[known gap <description>] <exception summary>`.
  - This supersedes the quoted wording in the Decision.
