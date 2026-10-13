# Explainer: step 17, the CI database job

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/17-ci-db-matrix`, five commits on the `feature/multi-db` tip after step 18 (`6dcae752eb`).
  - `37735b0c89` Make the database sentinel check the provider a job asked for
  - `e541bca2bc` Add run settings that select PostgreSQL for the database tests
  - `6ab8dcf9c7` Run the database tests against a PostgreSQL container in CI
  - `8e17261190` Mount the PostgreSQL test storage where every image keeps it
  - `3243ed4af2` Run the database tests nightly on the newest PostgreSQL
- **Status:** Gated: FAIL on the first review (the tmpfs blocker below, plus a check name that contained the version), PASS WITH NITS on the re-review. All nits folded in with red/green evidence, then merged into `feature/multi-db`. Not pushed. Backups: `multidb/17-backup-a` (first version, with a service container), `-b` and `-c` (before the rebases), `-d` (the version that failed review), `-e` (before the last nits).
- **Never run on GitHub.** None of these workflows has run on GitHub. Everything below was proved locally by running the job's steps in a simulation. Proving them on GitHub needs a push to the fork, and that is the user's decision.

## Why it exists

CI ran the database test projects on SQLite only, so a PostgreSQL regression would have gone unnoticed until someone ran the tests by hand. A CI job that claims to test PostgreSQL also has to prove it did. A lost environment variable makes the harness fall back to SQLite, and a server the tests did not start makes the `pg_dump` tests skip. Either way, the job can be green without having tested what it names.

## What changes, commit by commit

- **Sentinel checks the requested provider.** `ProviderBackend_IsLive` (the ADR-0010 name; the plan's earlier name was `DatabaseBackend_IsLive`) used to check only that the resolved backend met its version floor. With `JELLYFIN_TEST_EXPECTED_PROVIDER` set, it now also fails if the backend is a different provider, or if the expected name is unknown. That variable is separate from `JELLYFIN_TEST_DB_PROVIDER`, which does the selecting, so losing the selector cannot also silence the check. The integration factory's provider proof uses the same check (`BackendLiveness.VerifyProvider`, which lives in `Jellyfin.Database.Testing`), so the integration project also proves the provider the job asked for, not just the one that happened to be selected. Without the variable, nothing changes. The sentinel writes `Database backend: <provider> <version>` to the test output.
- **Run settings.** `tests/postgres.runsettings` sets `JELLYFIN_TEST_DB_PROVIDER=Postgres` for the test host, so `dotnet test --settings` or an IDE selects PostgreSQL without a shell export.
- **The CI job (ADR-0010 option 3).** `ci-tests-db.yml` is a reusable workflow (`workflow_call`) that takes one input, a JSON matrix. Each entry has the fields `provider`, `version` (the server major version), `image`, `imageVariable`, `settings` and `requiredTests`.
  - `ci-tests.yml` calls it as job `run-tests-db` with one entry: PostgreSQL 16.15, pinned by digest.
  - A separate step pulls the image, so a registry failure shows up as an infrastructure failure.
  - The tests start the container themselves through Testcontainers, the same path as a developer's machine. There is no service container and no connection string, which is why the `pg_dump` tests run.
  - The job runs every test project that references `Jellyfin.Database.Testing`. Since step 18, that includes the integration tests.
  - A final step reads the TRX files and fails unless all of these hold:
    - the sentinel passed;
    - every test in `requiredTests` passed (today, the `pg_dump` restore test);
    - the output reports the expected provider and major version.
  - Each entry also has a versionless `name` (the job name, so the required check stays `run-tests-db / Postgres` across version bumps) and an optional `timeoutMinutes` (default 30).
  - `imageVariable` must equal `JELLYFIN_TEST_<PROVIDER>_IMAGE`, the exact name the harness reads, so a typo or another provider's variable cannot silently fall back to the harness's undigested default tag.
  - The pull step also pulls the Testcontainers cleanup image (Ryuk 0.14.0, the Testcontainers 4.16.0 default, pinned by digest), so a registry failure for it also shows up there.
  - `requiredTests` matches theory cases as well as facts. Each name must be a plain identifier (`^[A-Za-z0-9_]+$`) before it is used in a pattern. For PostgreSQL, the required tests are the `pg_dump` restore test and the integration factory's provider proof, `CreateClient_SelectedProvider_RunsTheServerOnThatProvider`.
  - When the job fails or is cancelled, for example by its timeout, the TRX files are uploaded as an artifact (14 days).
  - Permissions are `contents: read`.
- **tmpfs mount for every image.** The harness put the test data on a tmpfs at `/var/lib/postgresql/data`. Images from 18 on keep their data under `/var/lib/postgresql/<major>/docker` and refuse that mount: the container exits at start and 447 tests fail.
  - The first fix moved the tmpfs to `/var/lib/postgresql`. Review caught that this was wrong for older images. They declare `VOLUME /var/lib/postgresql/data`, so Docker mounts an anonymous volume over the data directory, under the tmpfs. On 16.15 the data landed on ext4: the required job lost in-memory storage, and the commit message's "still lives in memory" claim was false.
  - The final fix keeps the tmpfs at `/var/lib/postgresql` and sets `PGDATA=/var/lib/postgresql/pgdata`, a path no image declares as a volume. The data is on tmpfs on both 16.15 and 18.6. A test pins `data_directory` so the layout cannot drift back.
- **Nightly on the newest release.** `ci-tests-db-nightly.yml` runs on a schedule, and can also be started by hand. It calls the same reusable workflow with PostgreSQL 18.6, pinned by digest, under the job name `Postgres newest`. A distinct name means a hand-started nightly cannot post a check that branch protection counts as the pull-request check.

## Decisions

- **No service container.** The first version used a GitHub service container. That contradicted ADR-0010 and silently skipped the `pg_dump` tests, so it was sent back for rework.
- **The TRX check over a new harness member.** Making the sentinel check `pg_dump` would have needed a provider-specific member on the generic backend contract. Reading the TRX for named tests keeps the contract generic, and lets each engine list its own required tests.
- **`version` is the major only.** A patch bump, for example by Renovate, then does not rename the check. SQL Server will need `"16"`, the major version it reports, not `"2022"`.
- **The timeout defaults to 30 minutes.** Locally the whole job takes about 4 minutes, and a cold GitHub runner is estimated at 10 to 20. If the first real run takes more than about 20 minutes, raise it to 40. SQL Server can set `timeoutMinutes` in its entry.

## Evidence

- Per commit: Release builds with 0 warnings and `git diff --check` is clean. At the tip, the Debug warnings match the base, and actionlint (with shellcheck) passes on all three workflows.
- SQLite: Implementations 1577 passed, 85 skipped; Server 85 passed, 7 skipped; Integration 132 passed, 11 skipped. The 24 more passing than step 16 are the new sentinel and `VerifyProvider` tests. The extra skip is the data-directory test, which is PostgreSQL-only.
- PostgreSQL 16.15 job simulation: pull, tests and check all pass, and the backend reports 16.15.
  - Implementations: 1609 passed, 50 skipped. The whole job took 247 s.
  - Integration: 139 passed, 6 skipped.
  - Server: 92 passed.
- Nightly simulation on 18.6: the same counts, passing in 226 s, with the backend reporting 18.6. `findmnt` shows the data directory on tmpfs on both versions, and on ext4 on 16.15 without the fix.
- Negative cases, each failing as intended:
  - an image that cannot be pulled fails the pull step, and no tests run;
  - a run where the dump test did not execute fails the check step;
  - the wrong expected version fails it;
  - an unknown required test fails it, both fact-style and theory-style;
  - a misspelt or wrong-provider `imageVariable` fails the pull step, and so does a required-test name that is not a plain identifier;
  - the integration provider proof, run on SQLite with `Postgres` expected, fails (it passed there before this change).
- Review checks: the reviewer measured tmpfs on both images in the test container, ran the check script against made-up TRX files (passing facts and theories; a theory with one case not run, a skipped test, a failed test, a missing test, a partial name match, the wrong major version, no TRX files), and confirmed that the Ryuk pre-pull is the exact image Testcontainers 4.16.0 uses.

## Not verifiable locally

- The exact required-check name. I expect `run-tests-db / Postgres`: the caller job plus the called job.
- The artifact upload, the `if: failure()` condition and the timeout expression were linted, but have never run.
- Cold-runner timing.
- Whether Docker and the Testcontainers cleanup container (Ryuk) behave on hosted runners as they do locally.
- The nightly schedule uses the fork's Actions minutes, and scheduled workflows may need to be enabled on the fork. GitHub only fires a schedule from the default branch, so the nightly does not run while this work sits on `feature/multi-db`. A manual dispatch works from any branch.

## Limits

- `SDK_VERSION` now appears in both `ci-tests.yml` and `ci-tests-db.yml`, because the `env` context is not available in a reusable workflow's `with:`.
- Whether the check is required is a repository setting, not part of this change.
- A known quirk, not from this step: a fresh worktree built with `--no-incremental` can fail with CS0234 on `Model.MimeTypes`. The actionlint findings in the other workflow files were also there before.
- MySQL, MariaDB and SQL Server entries come with their providers. They will need their own image variable and `requiredTests`.
