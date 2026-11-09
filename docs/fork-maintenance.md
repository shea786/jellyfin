# Maintaining the multi-database fork

This document records the maintenance plan for [shea786/jellyfin](https://github.com/shea786/jellyfin).
The fork's development and releases can proceed independently while individual upstream contributions are
discussed with Jellyfin maintainers. The upstream project retains its own roadmap and review decisions.

## Maintenance foundation

The preparation branch is `fork/maintenance-foundation`. It contains the complete `feature/multi-db` implementation
and architecture records, plus the eight reviewed follow-up commits through `6dda5e1621` from the upstream PR branch.
The runtime implementation matches `multidb/upstream-all` at that commit.

This foundation was built on upstream development commit `c7ce6a1b56`, not a stable release.
It preserves the existing work while the database changes are ported to the stable baseline described below.

The stable port is prepared on `port/stable-v12.4`, starting at upstream tag `v12.4`
(`83d0d0e7f76e23e2e085c8ff67d28b772d35967d`). Its code and package versions retain that release's upstream fixes.
The development-based foundation remains available as history; only the fork's changes are applied to the stable tag.
See the [v12.4 qualification record](database/qualification/v12.4.md) for tested commits, CI and installation evidence.

The maintained/default branch is `multidb/main`. The first preview is
[`v12.4.0-multidb.preview.1`](releases/v12.4.0-multidb.preview.1.md); its release notes describe the qualified upgrade
path and remaining limits. Support tiers are maintenance targets and do not turn a preview into a production release.

## Database targets

Keep the existing decisions from [ADR-0011](adr/0011-supported-database-targets.md):

| Database | Maintenance tier | Minimum version |
| --- | --- | --- |
| SQLite | Supported, default | Bundled version |
| PostgreSQL | Supported | 16 |
| MySQL | Experimental | 8.4 |
| MariaDB | Experimental | 11.4.7 |
| SQL Server | Experimental | 2022 |

Every release must retain SQLite functionality. PostgreSQL is the first external database to qualify for a fork
release. Experimental providers retain their startup warnings and documented known gaps; promotion requires the
evidence and dependency review described in ADR-0011.

## Stable release policy

Maintained fork releases must use the latest published, non-prerelease Jellyfin release as their upstream baseline.
Resolve the latest stable release when preparing an update and check again before publishing the fork release.
Pin the exact upstream tag and commit in the release notes and build metadata so every build remains reproducible.
As checked on 2026-11-09, the initial target is [Jellyfin v12.4](https://github.com/jellyfin/jellyfin/releases/tag/v12.4).
This dated target must be checked again before release; it is not a permanent version pin.

Upstream `master`, release candidates and moving release-branch tips are not release baselines. Updates to a new
stable release must pass review, supported-database CI and upgrade/restore qualification before publication.
The policy selects the latest stable target; it does not automatically deploy updates or certify them as tested.

## Branches and upstream updates

Use the following layout for fork maintenance:

- `multidb/main`: maintained default branch, based on the qualified stable port. Changes go through pull requests
  with required formatting, SQLite and PostgreSQL checks.
- `master`: retain for upstream development tracking and comparison; do not use it as the release baseline.
- `fix/*` and `feature/*`: short-lived changes reviewed against `multidb/main`.
- `sync/upstream-<stable-tag>`: stable-release updates prepared and tested before merging into the maintained branch.
- `fork/maintenance-foundation`: preserve the development-based implementation and architecture records as porting history.
- `feature/multi-db`, `multidb/upstream` and `multidb/upstream-all`: retain as development and submission history.

The initial port starts from the latest stable upstream tag on a preparation branch and applies the fork's database
changes selectively from the maintenance foundation. Do not merge the entire development-based foundation into the
stable branch: that would also import unreleased upstream changes. Review the resulting changes against the stable tag.

Subsequent upstream updates start from `multidb/main`, fetch the latest stable release tag and integrate that tagged
history on a synchronization branch. Review entity-model changes, migrations, provider conventions, queries and
dependency versions before running the database matrix. Preserve upstream attribution and published fork history.

Upstream releases and security fixes should be reviewed promptly. Scheduled checks can identify changes, but a
maintainer reviews and merges each synchronization; database migrations and dependency upgrades need explicit review.

## CI and repository activation

The existing workflows already cover unit tests, formatting, provider tests, OpenAPI, ABI and CodeQL. Before changing
the default branch, adapt the push and release triggers for the maintained branch and inspect the workflows inherited
from upstream. Some bot jobs require upstream-only secrets; the release version-bump workflow also assumes upstream
release branches and a Jellyfin bot identity.

The Tests and Format workflows run on pushes to `port/stable-*` and `multidb/main`, and can also be run manually.
They also run on pull requests inside the fork, independently of upstream's fork-workflow approval process.
Keep the workflows' current read-only token default.

Branch protection requires these GitHub Actions checks on the maintained branch:

- `format-check`.
- `run-tests (ubuntu-latest)`, `run-tests (macos-latest)` and `run-tests (windows-latest)`.
- `run-tests-db / Postgres`, including the backend-liveness sentinels, migration/model parity and generated docs.

Branches must be up to date before merging. Pull requests are required; the sole maintainer can merge a passing PR
without an additional approving reviewer. Admins are subject to the same required checks. OpenAPI, ABI and CodeQL
continue to provide additional change review; experimental database and newest-version jobs are not required checks.

Run the experimental database jobs and record their failures and accepted gaps. Their experimental status remains
visible; failures in supported SQLite or PostgreSQL tests block a release. Newest-version advisory jobs do not
establish a new minimum supported version by themselves.

The fork has its own issue tracker, Discussions and private vulnerability reporting. Fork-specific bugs and support
requests belong there. Keep upstream links and credits in the inherited documentation, with the fork notice providing
the entry point for this implementation.

## First preview release

Qualify a preview before describing a build as a maintained release:

1. Verify the chosen upstream baseline is still the latest stable release and record its tag and commit. Run a fresh
   full CI matrix on the chosen fork release commit, including SQLite and PostgreSQL. Earlier targeted test
   passes are useful evidence, but do not replace this run.
2. Test a fresh SQLite installation and an upgrade of a copy of an existing SQLite library.
3. Test a fresh PostgreSQL installation and SQLite-to-PostgreSQL backup/restore on a disposable library copy.
4. Check users, watched/resume state, row counts, artwork, playback and an insert after restoration. Record the tested
   server, client, database and image versions with the results.
5. Build an immutable fork image with the matching web client and FFmpeg. Publish it under the fork owner's registry
   namespace and use tags that distinguish the fork from official Jellyfin images.
6. Review the inherited release workflows before publishing a tag or release. Include upgrade instructions, known
   gaps and database-backup requirements. A database rollback requires a compatible backup as well as a server image.

The initial release can be PostgreSQL-focused while retaining the other providers as experimental. Publishing an
image or changing a running installation follows release qualification; maintenance-branch preparation alone does
not qualify a deployment.

## Upstream coordination

The upstream conversation is a separate workstream. Before proposing another provider series, establish which
direction Jellyfin maintainers want to support. Useful questions for the maintainer's own discussion are:

- Is coordination with the existing experimental PostgreSQL plugin preferred to an in-tree provider?
- Which concrete provider-contract or SQLite correctness changes would be useful independently?
- What is the smallest acceptable first change, and what evidence should accompany it?
- Who would review it, and what ongoing CI and maintenance responsibility would the fork take on?

Existing context:

- [Jellyfin's contribution process](https://jellyfin.org/docs/general/contributing/development/#feature-proposals).
- [Maintainer explanation of experimental provider plugins](https://github.com/jellyfin/jellyfin/discussions/12844).
- [Further database-provider discussion](https://github.com/orgs/jellyfin/discussions/17211).
- [The closed multi-database PR and its scope feedback](https://github.com/jellyfin/jellyfin/pull/18505#issuecomment-6358683479).

Any upstream communication should explain the maintainer's own goals and understanding, in their own words, as
required by [Jellyfin's communication policy](https://jellyfin.org/docs/general/contributing/llm-policies/).
Keep the original submission closed while discussing a new direction. Submit standalone bug fixes only when they
have a clear reproducer, focused implementation and tests, and fit the upstream process.


## Publishing fork previews

Preview tags follow `v<upstream-server-version>-multidb.preview.<number>`, initially
`v12.4.0-multidb.preview.1`. Add release notes in `docs/releases/<tag>.md`, merge the candidate through the maintained
branch, and wait for all five required GitHub Actions checks to pass on its exact commit before tagging it.
The first activation bootstraps `multidb/main` from the already qualified port before branch protection is enabled.

The `Fork Preview` workflow validates the tag, maintained-branch ancestry, recorded upstream tag/commit and the
latest stable upstream release through GitHub. It refuses publication if a newer stable baseline exists or required
CI has not passed. `.github/fork-release.json` records the baseline and required checks; update it together with the
stable port, image/runtime pins, release notes and qualification evidence for subsequent upstream releases.

The workflow uses only its repository-scoped `GITHUB_TOKEN`, with contents/checks/packages permissions declared in
that job. It publishes a versioned Linux amd64 image to `ghcr.io/shea786/jellyfin-multidb`, a public GitHub prerelease,
a downloadable Docker image archive, a provenance manifest and checksums. It does not publish a `latest` tag or
change a running installation. New GHCR packages must have public visibility before advertising anonymous pulls.

Upstream bot commands, issue triage, version bumps, OpenAPI publishing and bot comments are guarded to run only in
`jellyfin/jellyfin`. No Jellyfin bot token, release identity or upstream infrastructure is required by fork publishing.
