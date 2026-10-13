# Explainer: step 16, tests on the migrated schema

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/16-tests-on-migrated-schema`, four commits on the `feature/multi-db` tip after step 15 (`c265d3834f`).
  - `d67ba4a0a4` Copy SQLite test databases from a template
  - `ea9073742e` Build the default test database from the migrations
  - `6e9951cfe2` Leave the model test schema as the model derives it
  - `14b911558e` Stop exposing the PostgreSQL internals to the test harness
- **Status:** gated (PASS WITH NITS; three message and comment nits folded) and merged into `feature/multi-db`. Not pushed. Backup of the gated version: `multidb/16-backup-a` (`2857db8851`).

## Why it exists

Until now a test that did not ask for a schema got the one EF Core derives from the model (`EnsureCreated`). No server ever has that database. A server's schema comes from its provider's migrations, which also create things the model cannot describe: the migration history, the index on `lower("Name")` for people, on PostgreSQL the `min(uuid)` aggregate, and on SQLite a few column defaults and the migrations lock table. Tests made up for the gaps by hand: some applied single migrations, and the PostgreSQL model template installed the aggregate. A test can only show what a server does if it runs on the server's schema.

## What changes, commit by commit

- **SQLite template copy.** Applying every SQLite migration per test would be slow. The SQLite backend now builds each schema source once per process into an in-memory template and copies it into a fresh in-memory database per test with SQLite's backup API, the same shape as the PostgreSQL backend's template clone. Copies are made one at a time because the template connection serves one caller at a time. The gate measured the SQLite suite at 47 s before and 40 s after.
- **Default flip.** `DatabaseTestFixture` without a schema source now uses `TestSchemaSource.Migrations`. Four new `DefaultSchemaTests` pin that a default database has the migration history and the people name index on both providers, and the uuid aggregate on PostgreSQL; they fail if the default goes back to the model. Seven PostgreSQL test assumptions were corrected, none loosened (the gate found one now stricter):
  - the convention tests exclude `__EFMigrationsHistory` by name from the `varchar` and identifier checks, because its columns and key belong to EF's history repository, not to the model;
  - the NULLS FIRST index rule excludes `IX_Peoples_NameLower` by name: its expression is `lower("Name")`, `Name` is NOT NULL, so the expression is never NULL and its null order cannot matter;
  - four provider tests now set up the migration history they need instead of relying on an empty table.
- **Model stays the model.** The PostgreSQL model template no longer installs the aggregate. `TestSchemaSource.Model` is documented as exactly what EF derives, with what it lacks; `Migrations` as what a server has. Only three tests still ask for the model on purpose: the model-against-migrations comparison and two backend self-tests.
- **No harness access to provider internals.** The harness reached into the PostgreSQL provider only to install the aggregate. With that gone, the `InternalsVisibleTo` grant is removed.

## Decisions

- **Plan B5 said this step would bring `IX_Peoples_NameLower` to model-source databases. It does not** (plan amendment B11). A model database with part of the migrated schema added would describe neither schema. Everything that needs the index runs on the default, which is now the migrated schema.
- **The flip found no bug** in a migration or the model. That is a result: the PostgreSQL baseline and the SQLite migrations both produce a schema the existing suite passes on.

## Evidence

- Every commit builds alone in Release with 0 warnings and passes on SQLite and PostgreSQL; Debug warnings match the base.
- Tests at the tip: SQLite Implementations 1553 passed, 84 skipped; Server 85 passed, 7 skipped; Integration 129 passed, 3 skipped. PostgreSQL Implementations 1584 passed, 50 skipped, 0 failed, twice; Server 92 passed. The only changes from step 15 are the four new tests: on SQLite two run and two are PostgreSQL-only; on PostgreSQL three run and one is SQLite-only. The gate confirmed this from the TRX name diff.
- `HasPendingModelChanges` is false on both providers. No migration file is added.

## Limits

- Hand-rolled tests that open their own SQLite connection and call `EnsureCreated` still run on the model schema. Step 11 moves them onto the fixture (plan B8).
- The SQLite template lives for the test process. A test that changes the schema works on its own copy and cannot affect the template.
