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
