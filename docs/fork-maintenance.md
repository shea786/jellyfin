# Maintaining the multi-database fork

This document records the maintenance plan for [shea786/jellyfin](https://github.com/shea786/jellyfin).
The fork's development and releases can proceed independently while individual upstream contributions are
discussed with Jellyfin maintainers. The upstream project retains its own roadmap and review decisions.

## Maintenance foundation

The preparation branch is `fork/maintenance-foundation`. It contains the complete `feature/multi-db` implementation
and architecture records, plus the eight reviewed follow-up commits through `6dda5e1621` from the upstream PR branch.
The runtime implementation matches `multidb/upstream-all` at that commit.

The fork currently has no published fork releases. The supported tiers below describe the validation and maintenance
targets; they do not certify a production release. The first release needs the qualification work listed below.

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

## Branches and upstream updates

Use the following layout when activating fork maintenance:

- `multidb/main`: proposed default branch for the maintained fork, seeded from the maintenance foundation.
- `master`: retain the existing upstream baseline for comparison and fork synchronization.
- `fix/*` and `feature/*`: short-lived changes reviewed against `multidb/main`.
- `sync/upstream-YYYY-MM-DD`: upstream updates prepared and tested before merging into the maintained branch.
- `feature/multi-db`, `multidb/upstream` and `multidb/upstream-all`: retain as development and submission history.

An upstream update starts from `multidb/main`, fetches `jellyfin/jellyfin:master`, and merges that upstream history
on the synchronization branch. Review entity-model changes, migrations, provider conventions, queries and dependency
versions before running the database matrix. Preserve upstream attribution and published fork history.

Upstream releases and security fixes should be reviewed promptly. Scheduled checks can identify changes, but a
maintainer reviews and merges each synchronization; database migrations and dependency upgrades need explicit review.

## CI and repository activation

The existing workflows already cover unit tests, formatting, provider tests, OpenAPI, ABI and CodeQL. Before changing
the default branch, adapt the push and release triggers for the maintained branch and inspect the workflows inherited
from upstream. Some bot jobs require upstream-only secrets; the release version-bump workflow also assumes upstream
release branches and a Jellyfin bot identity.

For the first full run, use pull requests inside this fork so its own CI results are available independently of
upstream's fork-workflow approval process. Keep the workflows' current read-only token default.

After the jobs have run successfully, configure required checks for the maintained branch:

- Formatting and the SQLite test suite.
- PostgreSQL tests, including the backend-liveness sentinel.
- Provider/migration parity and the generated operator-documentation check already run by the database workflow.
- OpenAPI, ABI and security checks where applicable to the change.

Run the experimental database jobs and record their failures and accepted gaps. Their experimental status remains
visible; failures in supported SQLite or PostgreSQL tests block a release. Newest-version advisory jobs do not
establish a new minimum supported version by themselves.

Enable the fork's issue tracker and Discussions when activating the maintained branch. Fork-specific bugs and support
requests belong there. Keep upstream links and credits in the inherited documentation, with the fork notice providing
the entry point for this implementation.

## First preview release

Qualify a preview before describing a build as a maintained release:

1. Run a fresh full CI matrix on the chosen release commit, including SQLite and PostgreSQL. Earlier targeted test
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
