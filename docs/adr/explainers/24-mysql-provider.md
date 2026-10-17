# Explainer: step 24, the MySQL and MariaDB provider

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/24-mysql-provider`, 14 commits on the `feature/multi-db` tip `258cf7455c`, final tip `82f4a31b54`.
  - `001aba957d` Add the MySQL and MariaDB database provider
  - `217ebb2498` Seed test databases through EF's migration pipeline
  - `cfa0c05608` Store unindexed text as longtext on MySQL and MariaDB
  - `4be9e3fed1` Reset MySQL identity counters from the column metadata
  - `a9e83f6d57` Add MySQL and MariaDB test backends to the pool
  - `219ce37ab3` Make five shared database tests engine-neutral
  - `f737dbd663` Rank search results with an integer score in the query
  - `e4b1be12d1` Clear the owner and parent of a deleted item before the delete
  - `9e5e438372` Create the migration history before the restore transaction
  - `d1c602bccb` Test the identity counters of MySQL and MariaDB around a restore
  - `6cdcaaeff6` Match the display preferences statements on every engine
  - `da3f40a89d` Record three MySQL and MariaDB differences in the known-gap ledger
  - `beb898ac47` Refuse to restore a backup into a database that needs migrating
  - `82f4a31b54` Run the migration tests on MySQL and MariaDB where they can
- **Status:** architecture gate (PASS WITH FIXES), code review (three must-fix), delta gate (APPROVE WITH NITS), then two fold-in rounds. Merged into `feature/multi-db`. Not pushed. Backups: `multidb/24-backup-g` (before the review fixes), `-e` (the gated tip `7d4c0b19d3`), `-h` (the round 1 tip `735202eb3e`).
- **Relevant decisions:** ADR-0011 F1 to F6, ADR-0009 L6, ADR-0003 S3 to S5, ADR-0008 S4, ADR-0006 C7, ADR-0005 R16, ADR-0002 H14.
- **The provider is not registered and has no migrations.** A running server cannot select it yet; step 25 does that.

## Why it exists

ADR-0011 names MySQL 8.4 and MariaDB 11.4.7 as supported targets, served by one provider. This step adds that provider and runs the shared test suite on both servers. It does not register the provider and ships no migrations, so a running server cannot select it yet. Step 25 adds the baseline migration and the registration, and with that a server that starts on MySQL.

## What the provider does

- **One package, two servers.** `Microting.EntityFrameworkCore.MySql` 10.0.12, pinned exactly. The server comes from the `server-version` option in `database.xml` or from the version the server reports. The provider refuses a server below the floor (MySQL 8.4, MariaDB 11.4.7) and a reported version it cannot read, unless the option names the server.
- **Conventions, so the shared model needs no MySQL code:**
  - every text column uses `utf8mb4_0900_bin`, NO PAD and ordered by code point, like `"C"` on PostgreSQL; the connection is forced to `utf8mb4`;
  - a `Guid` is `binary(16)`;
  - a `DateTime` is `datetime(6)` in UTC, and `DateTime.MaxValue` is clamped to the last value the column can hold, with `MinValue` and `MaxValue` treated as UTC whatever their kind;
  - indexed text gets explicit prefix lengths so each index fits in 3072 bytes; unindexed text is `longtext`;
  - index filters are removed (the servers have no partial indexes), and identifiers are cut to 64 characters.
- **Session settings.** An interceptor sets strict `sql_mode`, READ COMMITTED and UTC on every connection, so the server never truncates silently.
- **Lists of ids** are sent as one parameter per value, because the JSON path of the package does not work on these servers.
- **Case-insensitive matching** folds ASCII letters only, as on SQLite and PostgreSQL. `LOWER()` alone would also fold `É` and the Kelvin sign.
- **Write conflicts.** A duplicate key (1062), a deadlock (1213) and a lock wait timeout (1205) are classified as write conflicts, so the retry from step 07 runs the work again in a new transaction.
- **Migrations are not transactional** on these servers: DDL commits implicitly. So `SupportsTransactionalMigrations` is false, and the migration-in-progress marker from step 21 protects the database. There is no fast backup; the operator's `mysqldump` is the way back.

## Shared code that changed, and why it is safe on SQLite and PostgreSQL

- **Test seeding through EF's migration pipeline.** A reused test database gets its seed rows from the insert operations of the design-time model, written by the provider's own migrations generator, so keys the database generates are handled the way each engine needs.
- **The search score is an integer computed in the query.** EF writes a float constant with no fraction as an integer literal, and MySqlConnector then returns a long, which threw when read as a float. The order of results does not change on any engine.
- **Deleting an item clears `OwnerId` and `ParentId` of the deleted rows first.** MySQL and MariaDB check a foreign key per row and stop a cascade nested more than 15 levels deep, which a deep folder tree reaches. On SQLite and PostgreSQL it is one extra `UPDATE` with the same result. The library migration routine does the same before it empties `BaseItems`.
- **Restore creates the migration history before its transaction,** and refuses, before writing anything, when the marker exists or migrations are pending on an engine that cannot roll them back.
- **The display preferences test** matches the statements by shape, not by SQLite's double quotes, since MySQL quotes with backticks and MariaDB writes the delete differently. Test code only.

## What the gates found and what was done

- **Architecture gate (PASS WITH FIXES).** It accepted four deviations, now in the ADRs: the 512-character prefix cap and the refusal of uniqueness over a prefix (ADR-0009 L6); the identity reset over every identity table, skipped inside a caller's transaction (ADR-0011 F4); no fast backup (F5). It asked for a race test that really races, a MySQL identity-after-restore test, stable gap ids and a restore test with over-long values. All were folded in.
- **Code review (three must-fix):**
  - M1: a failed migration on MySQL asked for a rollback that could not happen, and a refused restore pointed at the archive. Now no rollback is asked for without a snapshot, and every message points at the engine's dump (ADR-0003 S3, S4).
  - M2: `MinValue` and `MaxValue` moved by the host's offset when their kind was Local. Now they are UTC whatever their kind; the time-zone matrix runs four zones.
  - M3: a delete of a deep folder tree failed on the 15-level cascade limit. Now the parent is cleared first; tests delete 20- and 40-level chains on all four engines.
- **Delta gate (APPROVE WITH NITS).** A commit title that hid the identity change, a test that could not fail, a misplaced helper, a rollback message that claimed more than was true, and gaps in the per-commit record. All were folded in, and the per-commit run now records every check for every commit.

## Known gaps on these servers

Recorded in the ledger, each keyed to its engine:

- `MARIADB-OUTER-REF-DERIVED`: MariaDB cannot resolve a derived table that refers to the outer item, which the `DatePlayed` sort uses. Step 24b removed it with a MariaDB-only query rewrite (ADR-0011 F7).
- `MYSQL-SORTNAME-RANGE-CASE`, `MARIADB-SORTNAME-RANGE-CASE`: the sort name range filters with a non-ASCII term (ADR-0005 R16).
- Lists of ids larger than `max_allowed_packet` (ADR-0011 F2), and MariaDB's `max_sort_length` (ADR-0009 L6), are documented limits.

## Limits

- Not registered and no migrations: the suite runs against a schema built from the model. Tests that need EF migrations are skipped on these servers and listed for step 25.
- A failed upgrade can only be undone from the operator's own dump.
- A save behind a lock that is never released fails after about three lock wait timeouts, about 150 seconds at the default (ADR-0008 S4).

## Counts (passed/skipped, 0 failures)

At the tip `82f4a31b54`:

| Engine | Implementations | Server.Tests |
|---|---|---|
| SQLite | 2163/103 | 147/8 |
| PostgreSQL | 2195/68 | 142/13 |
| MySQL | 2100/147 | 100/54 |
| MariaDB | 2099/148 | 100/54 |

SQLite Integration 137/11. The time-zone matrix (UTC, America/New_York, Asia/Kolkata, Europe/London) passes on SQLite (164/8) and MySQL (170/2). Release has 0 warnings; Debug warnings equal the base apart from the accepted `SessionManager` line shift. Every one of the 14 commits builds and passes on its own on SQLite and on PostgreSQL (see the procedure note in `pr-plan.md` about per-commit builds). Logs: `/tmp/step24/fold1/` and `/tmp/step24/fold2/`.

## Things worth knowing when defending it

- The counters test after a restore was first written so that it could not fail: it passed with a reset added to the restore path. It now reads the highest ids the target had handed out and checks that new rows are above them; with the reset added it fails on both servers, and `fold2/mutation.log` shows that.
- The split of the history change from the identity tests shows what depends on what: only the two existing rollback tests need the history to be created before the transaction.
- The migration tests that need EF migrations are skipped on these servers until step 25 (a checklist of 32 tests is in `/tmp/step24/skips/step25-unskip-checklist.txt`).
