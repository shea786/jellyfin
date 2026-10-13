# Explainer: the database test harness and the PostgreSQL provider

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/wip-infra`, eight commits, rebased onto `feature/multi-db` after step 12 so that the provider's error message, which names `database:ConnectionString`, is true at every commit. In the final series these are steps 10 and 13: the test infrastructure, then the PostgreSQL provider with its test backend.
  - `f791dd5a83` Add cross-provider database test infrastructure
  - `602fdbcc53` Rename SqliteDbTestFixture to JellyfinDbTestFixture
  - `c167218934` Tag the model snapshot test with the Migrations category
  - `e1ab212c87` Add PostgreSQL database provider
  - `30d7aadd3a` Classify PostgreSQL write conflicts as retryable
  - `2cd6f6cb5d` Add PostgreSQL test database backend
  - `d0fa655803` Reset PostgreSQL identity sequences after a restore
  - `ad45c80b48` Mark known PostgreSQL gaps in database tests
- **Status:** gated and merged into `feature/multi-db`. Not pushed. Backups: `multidb/wip-infra-backup-r3`, `-r4`, `-r5` (the version before the gate fixes), `multidb/wip-infra-pre-reorder`.

## Why it exists

Every test that needed a database built its own in-memory SQLite database, so nothing could show whether a change works on another engine. Before any PostgreSQL convention, migration or query fix can be trusted, the same tests have to run against PostgreSQL. Known differences must not drown out real regressions.

## What changes

- **One switch.** `JELLYFIN_TEST_DB_PROVIDER` picks the provider for the whole run. When it is unset the provider is SQLite, so nothing changes for upstream CI. An unknown value throws instead of silently falling back.
- **Backends.** `Jellyfin.Database.Testing` holds `ITestDatabaseBackend` with one implementation per engine and a single provider map. Each test instance gets its own database. The PostgreSQL backend starts one Testcontainers server per run (`postgres:16.15-trixie`), or uses `JELLYFIN_TEST_POSTGRES_CONNECTIONSTRING`. It builds the schema once into a template database and gives every test a copy. Copies are dropped on dispose, and leftovers are dropped at process exit.
- **Restricting tests.** `ProviderFact` and `ProviderTheory` limit a test to named providers. They are used for tests of SQLite internals (extended result codes, `sqlite_sequence`, deferred foreign keys, staged SQLite lock races) and for tests that store a NUL character, which PostgreSQL text cannot hold.
- **Known gaps.** `KnownGap.On` / `OnAsync` wrap the smallest failing span of a test. On a provider with the gap, the expected failure becomes a skip. Each gap says what failure it expects: assertion failures for behavioural gaps, or a specific SQLSTATE (42883 uuid aggregate, 22021 NUL in a parameter, 22001 value too long). Any other exception still fails the test. The uuid aggregate gap also requires the message to name a uuid aggregate, and the migrations gap requires the backend's own message, so an unrelated error with the same code is never skipped. A wrapped body that passes also fails the test, so a fixed gap cannot leave a stale marker behind. On SQLite the wrapper does nothing.
- **The provider.** `Jellyfin-PostgreSQL` is added but not registered or shipped with migrations yet; that comes with the migrations step. It uses single-query includes for a consistent snapshot. The command timeout defaults to 60 s with a longer one for VACUUM/ANALYZE. The password is kept out of the logged connection string.
- **Write conflicts.** The provider answers the retry hook added by the write-conflict step: 23505 (the insert race the retry exists for), 40001 (only at stricter isolation levels, which the server owner may choose) and 40P01 (deadlock) are retryable. Everything else stays fatal. That includes foreign key, not null and check violations, and also connection errors and timeouts, where repeating a statement that may already have been applied is not safe.
- **Restore.** A restore inserts rows with their original ids, which does not advance PostgreSQL's identity sequences. The next new row then collided with a restored one. The provider's `CompleteDatabaseRestoreAsync` sets each identity sequence of the model to the table's highest id, or back to 1 when the table is empty. It runs inside the restore transaction. `setval` itself is not transactional; a rollback restores the sequences only because the purge's `TRUNCATE ... RESTART IDENTITY` ran earlier in the same transaction.

## Results on PostgreSQL

- At the tip, Implementations tests: 1377 passed, 0 failed, 151 skipped, identical in two runs. The skips are 88 GuidAggregates, 33 SQLite-only, 12 Windows-only, 10 StringCollation, 3 NulInExactMatchParameter, 2 StringColumnLength, 1 NullOrdering, 1 UnicodeLower and 1 PostgresMigrations. On SQLite the same tree gives 1508 passed and 36 skipped, Server 69, Integration 129 passed and 3 skipped.
- The gaps are closed by later steps: the conventions step (collation, null ordering, lower-case folding, column length), the uuid aggregate work and the migrations step.
- The earlier steps' tests on PostgreSQL: backup and restore (36), write-conflict retry and user concurrency pass. The NUL search tests wait behind GuidAggregates.
- New PostgreSQL tests: classification 24, real server violations 9 (including an actual serialization failure and a deadlock between two transactions), restore identity 4.

## Evidence

- Every commit: Release 0 warnings, Debug warnings identical to the base, SQLite tests green. The rename library test passed 50 of 50 on SQLite (5 runs of 10).
- Mutations: breaking the 23505 mapping fails 6 tests, and removing the sequence reset fails 1. Widening the two skip predicates back to any 42883 or any NotSupportedException fails 4 of the new infrastructure tests.
- The exact-match NUL test is split in two so that SQLite still runs the grouped path; the grouped half is a uuid aggregate gap on PostgreSQL, which is why that count is 88.

## Limits

- Server.Tests and the integration tests still run on SQLite only. Several test classes still open SQLite themselves. They are listed for step 11, which moves them onto the fixture.
- The empty-table restore test also passes without the fix, because the purge's `TRUNCATE ... RESTART IDENTITY` already resets the sequence. It guards against setting the sequence wrongly, not against the original bug, which the populated-table tests cover.
- `Jellyfin.Database.Testing` has public types. It is a test project, not packed or shipped, and not on the ABI list.
