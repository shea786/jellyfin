# Explainer: step 15, PostgreSQL migrations and registration

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/15-postgres-migrations-and-registration`, nine commits on the `feature/multi-db` tip after the PostgreSQL conventions merge (`174d2d2b3a`).
  - `03857c02e7` Add a design-time context factory for the PostgreSQL provider
  - `4f1cf7db44` Add the baseline migration of the PostgreSQL provider
  - `7c623eebe8` Index people by lowercased name on PostgreSQL
  - `aff0957734` Add min over uuid to PostgreSQL
  - `10ec29140a` Run the grouping and ordering tests on PostgreSQL
  - `8b7230a645` Warn about the missing PostgreSQL snapshot only after schema migrations
  - `bc830506dc` Register the PostgreSQL provider
  - `651c8dd720` Pin that a failing PostgreSQL migration leaves no partial state
  - `c6d841ddd7` Word the migration backup log line for every provider
- **Status:** gated (first gate PASS WITH NITS, six fixes folded, delta gate PASS WITH NITS; its three optional nits were not taken: the index drop is unqualified like every EF-generated drop, the function drop relies on its jf_ name, and one redundant exception filter stays) and merged into `feature/multi-db`. Not pushed. Backups of earlier versions: `multidb/15-backup-a`, `-b`, `-c`, `-d` (rebase stages), `-e` (`fbcd562fd8`, the version the first gate reviewed), `-f` (the fixup-squashed tree before the message reword).

## Why it exists

After step 14 the PostgreSQL provider behaved like SQLite where Jellyfin depends on it, but it still could not be started as a server. It had no migrations, so a database could only be created from the model by the tests; it was not registered, so `Jellyfin-PostgreSQL` in `database.xml` was an unknown type; and it lacked `min(uuid)`, so every query that groups items and takes the lowest id failed. This step closes those three, and with it the two test gaps that stood for them.

## What changes, commit by commit

- **Design-time factory.** The EF tools need a context without a running server. The factory builds it through the provider, so the conventions, collation and NULL ordering the server uses are the ones the tools see. The connection string is a placeholder host; generating a migration never connects.
- **Baseline migration.** One migration, `20200101000000_InitialCreate`, generated from the current model. Its identifier sorts before every SQLite migration and before every code routine, so it runs first on a new database. `PostgresMigrationBaseline.CoversThroughSqliteMigration` names the last SQLite migration the baseline already contains, and a test requires that every later SQLite migration gets a PostgreSQL one, with the SQLite migration's identifier. Another test builds one database from the migration and one from the model and compares columns, indexes and constraints. Because step 14 changed null sort orders, the baseline was regenerated on top of it; the only difference from the first draft is the null-order annotations (none on non-nullable columns).
- **People index.** The model cannot describe an index on `lower("Name")`, which SQLite gets from a migration with raw SQL. It is created from a hand-written part (`InitialCreate.HandWritten.cs`), so regenerating the baseline only requires putting back two calls. `CREATE INDEX IF NOT EXISTS`; a test checks the planner answers the lookup from it.
- **`min(uuid)`.** PostgreSQL has no such aggregate. The baseline creates an SQL function and a parallel-safe aggregate with a sort operator, in the schema the tables live in (`current_schema()`, quoted with `format('%I')`), so a `pg_dump` carries them and a non-public schema works. No superuser and no extension. Creation is skipped when `min(uuid)` already resolves, so a later PostgreSQL release is not shadowed. Down drops our aggregate only when it is built on our function, then the function with RESTRICT, never CASCADE: if a user's view depends on it, the revert fails with 2BP01 and leaves everything in place (chosen at the gate over silently dropping the view). There is no `max`; nothing asks for one. Byte order is the one SQLite's blob comparison gives, and NULLs are ignored as in any aggregate.
- **Grouping and ordering tests on PostgreSQL.** 88 cases that were skipped for the missing aggregate now run. 87 pass. The 88th, `GetItemList_PathContainingNul_MatchesNothing`, never reached its assertion before and now meets the NUL gap, so it moves there (the gap has four rows, not three). Most of the 88 group or order by an item id; the rest are NUL, name-contains and search-term tests that reach the same grouping query. The tests still build their schema from the model, so the template database installs the aggregate the way the migration does until step 16 switches the default to migrations.
- **Snapshot warning.** A fresh install records code routines before the schema exists; the warning to take a `pg_dump` counted them as applied migrations and fired on every first start. Only identifiers the provider's migration assembly knows count now.
- **Registration.** `Jellyfin-PostgreSQL` resolves to the PostgreSQL provider and appears in the unknown-type error. Migration service tests run against an empty database: a fresh install, a second run that finds nothing to do, a restart, and a set-up server on an empty database being refused. A missing connection string names three places to set one (the element in `database.xml`, `database:ConnectionString`, `JELLYFIN_DATABASE__CONNECTIONSTRING`), and tests follow each. A malformed string throws a clear `InvalidOperationException` with the same guidance, with no inner exception because Npgsql messages can echo keywords and values; a test with a fake password checks it never appears.
- **Atomicity tests.** On PostgreSQL there is no snapshot to restore, so the protection against a half-applied upgrade is that every migration runs in one transaction and that anything that cannot is refused. Two tests pin how the provider configures EF (one transaction per migration, `NonTransactional=Throw`) using test migrations; a third runs the real baseline against a database where a conflicting function already exists, so it fails after the tables were created, and asserts no Jellyfin table and no history row remain.
- **Backup log line.** The line said a migration would modify "the jellyfin.db" and, on PostgreSQL, afterwards claimed a backup under a marker key. A provider that took no snapshot now returns an empty string (documented on `MigrationBackupFast`, no signature change), the migration service logs "No backup ... was taken" for it, and the SQLite line is the upstream text unchanged. The service has no reference to a concrete provider.

## What the gap numbers mean

- **GuidAggregates is closed, which is not the same as "ordering verified".** What changed is that 87 of the 88 rows now run their assertions and pass: NullOrder (10), StringOrder (6), PlayedOrdering (5), PremiereDate (2), NameContains, SearchTerm and the NUL term tests among them. Before this step none of them could get past the missing aggregate, so step 14's collation and NULL-ordering claims were backed only by convention tests. They are now also backed by the repository tests running on PostgreSQL. One row moved to NulInExactMatchParameter.
- **PostgresMigrations is closed.** `Create_MigrationsSchema_ProducesUsableDatabase` passes; the restore-identity tests start from a migrations-built database, as a server's does.
- **What remains** on PostgreSQL: 33 SQLite-only tests, 12 Windows-only, and 4 NulInExactMatchParameter rows. PostgreSQL cannot store NUL in text; filters that compare a column to an exact value containing NUL would need the API boundary to reject or strip it. That is one fix for all four rows, including `Path`, and is deliberately outside this series.

## Evidence

- Builds: every commit builds alone in Release with 0 warnings and `git diff --check` clean; Debug warnings match the base list at the baseline commit and the tip.
- Tests at the tip: SQLite Implementations 1551 passed, 82 skipped; Server 85 passed, 7 skipped; Integration 129 passed, 3 skipped. PostgreSQL Implementations 1581 passed, 49 skipped, 0 failed, identical across two runs; PostgreSQL Server 92 passed. `has-pending-model-changes` reports no changes for both providers.
- Server check on `postgres:16.15-trixie`, Release build, fresh database:
  - first start applies the baseline, logs the `ALTER ROLE ... SET jit = off` once, and `pg_db_role_setting` shows `jit=off`;
  - the setup wizard, admin login (wrong password gives 401) and a library scan of four movies work, search and sort return the expected items;
  - second start has no migration lines and no `ALTER`; the role setting is unchanged. The already-off case logs nothing by design, so the evidence is the absence of the command;
  - after the restart items, counts, search and sort are identical;
  - a malformed connection string containing a fake password stops the start with the guidance message; the password is not in stdout, stderr or any log, data, config or cache file.
- Gate evidence: the gate applied the baseline's Up and Down by hand, saw a mutation that drops a unique flag fail the schema comparison test, saw a mid-baseline failure leave no tables and no history row, and saw that turning `NonTransactional=Throw` off fails a test.

## Limits

- No snapshot exists on PostgreSQL. A failed upgrade leaves the database at the last migration that applied completely; the only way back is the `pg_dump` the log asks for.
- Shipping the provider adds Npgsql and its dependencies to every build, as any in-tree provider does. They are loaded only when `database.xml` selects PostgreSQL.
- The baseline's hand-written part must be kept when the baseline is regenerated; the readme in the provider says how, and the schema comparison and index tests fail if it is lost.
- The aggregate is a user-level object in the Jellyfin schema. Restoring a database through `pg_dump` carries it; restoring only table data into a database not made by the migrations does not (the restore tests cover both paths that exist).
