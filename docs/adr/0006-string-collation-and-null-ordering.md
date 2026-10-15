# ADR-0006: String collation and deterministic ordering, including NULLs

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- **Strings.** SQLite `BINARY` compares UTF-8 bytes, which is code-point order. On PG the default collation comes from the `initdb` locale:
  - glibc images such as `postgres:16-trixie`, the test image from ADR-0002, sort linguistically with `en_US.utf8` (`a A b B …`);
  - Alpine/musl sorts byte-wise (verified: `A,B,Zeta,_x,a,b,e,élan`, the same as `"C"`).
  So ordering differs from one installation to the next.
- **Collation-sensitive code:**
  - ordering in `OrderMapper.cs:51-89` (`SortName`, `CleanName`, `SeriesName`, `Album`, first `CleanValue`) and `QueryBuilding.cs:306-383` (`ApplyOrder`, tiebreak on `Name`/`SortName`);
  - People ordering in `PeopleRepository.cs:43-56`;
  - range predicates, where `CompareTo` becomes `<`/`>=`, in `QueryBuilding.cs:231-243` and `PeopleRepository.cs:485-493`;
  - string `MIN` in `ByName.cs:144,170` and `Querying.cs:637,650`.
- **NULLs.** SQLite, MySQL and SQL Server treat NULL as the lowest value: first when ascending, last when descending. PG puts NULLs last when ascending. Verified: SQLite `_zed,alice,Ángel,Bob`, PG `Bob,_zed,alice,Ángel`. Affected nullable keys include `PremiereDate`, `DateCreated`, `CommunityRating`, `CriticRating`, `ProductionYear`, `IndexNumber`, `ParentIndexNumber`, `Album`, `SeriesName`, and the `MAX(LastPlayedDate)` subqueries (`OrderMapper.cs:42-48,80-87`).
- Npgsql 10 still has `ReverseNullOrdering` (it emits `NULLS FIRST`), but it is now internal: our probe failed to compile with CS0122.
- `ApplyOrder` has no unique final key, so the order of ties is unspecified on both engines.

## Options considered

- **Collation:**
  - (a) A database-level `LC_COLLATE=C`: we can't enforce it on databases that users provision themselves.
  - (b) Column-level `"C"` through a provider convention.
  - (c) ICU `und-x-icu`: linguistic, so it differs from SQLite.
  - (d) The builtin `pg_c_utf8`: PG 17+ only (the floor is 16), and its `lower()` is Unicode, which breaks ADR-0005.
  - (e) Nondeterministic collations: rejected in ADR-0005.
- **NULL ordering:**
  - (a) Shared LINQ that prepends `x == null ? 0 : 1` to every nullable key. It changes SQLite's SQL, and a `CASE` in `ORDER BY` defeats index-ordered scans on both engines.
  - (b) A PG-provider SQL generator that appends `NULLS FIRST` to ascending orderings and `NULLS LAST` to descending ones.
  - (c) Turning on the internal `ReverseNullOrdering` through reflection.
- **Tie-breaking:** append `ThenBy(Id)`, or leave tie order unspecified.

## Decision

- **Parity definition:**
  - strings compare and sort by Unicode code point, case-sensitively;
  - NULL is the lowest value;
  - rows that tie on every requested key come back in an unspecified order, and tests must not depend on it.
- **Collation (b):** the PG provider's `ConfigureConventions` calls `Properties<string>().UseCollation("C")`. ADR-0009 adds `HaveColumnType("text")`. Verified DDL: `text COLLATE "C"`, and the ordering matched SQLite.
- **NULLs (b):** the PG provider calls `ReplaceService<IQuerySqlGeneratorFactory, JellyfinNpgsqlQuerySqlGeneratorFactory>()`. Its generator subclasses `NpgsqlQuerySqlGenerator` and overrides `VisitOrdering` to append `NULLS FIRST`/`NULLS LAST`. Verified: same results as SQLite, including inside `ROW_NUMBER`/`LIMIT 1` subqueries.
- **Index null order.** A model-finalizing convention sets `NullSortOrder.NullsFirst` on PG index columns, so ordered index scans still apply. It is registered through `ConfigureConventions` like `DoNotUseReturningClauseConvention` (`SqliteDatabaseProvider.cs:282-285`), because provider `OnModelCreating` runs before the entity configurations (`JellyfinDbContext.cs:326-333`).
- **No `Id` tiebreak in v1.** It would change SQLite's hot-path plans. Pagination stability is a separate topic.
- **Shared code and SQLite are unchanged.**

## Consequences

- We depend on an Npgsql internal API: the `NpgsqlQuerySqlGenerator` constructor `(deps, typeMappingSource, bool reverseNullOrderingEnabled, Version)`. The parity tests catch breakage on upgrades. Follow-up: ask Npgsql to make `ReverseNullOrdering` public again, then delete our subclass.
- `"C"` is not linguistic (`Zeta` < `apple`, `élan` > `z`), exactly like SQLite. User-facing sorting uses the already lower-cased `SortName`.
- Bonus: under `"C"`, `LIKE 'prefix%'` can use plain btree indexes.
- MySQL can use `utf8mb4_bin`, and it already sorts NULL lowest. SQL Server `*_BIN2` on `nvarchar` orders by UTF-16 code unit, which differs for supplementary characters. That ADR has to decide.

## Tests required

**(a) SQLite (pin-only PR, no production change):**

1. `OrderBy_Album_CodePointOrder`: `Bob, alice, _zed, Ángel` sorts ascending as `Bob,_zed,alice,Ángel`. Same for `SeriesSortName`.
2. `NameStartsWithOrGreater`/`NameLessThan` with `_`, `é` and uppercase boundaries, on items and on People.
3. `ByName` string `MIN` picks the representative that is smallest by code point.
4. For each nullable `ItemSortBy` key above, ascending and descending: NULL rows come first ascending and last descending. Non-null values are distinct and `SortName`s are distinct.

**(b) Postgres:**

- 1-4 unchanged;
- `[PostgresFact]`: every string column has collation `C`;
- the SQL for an ordered query contains `NULLS FIRST`;
- run with `postgres:16-trixie`, so a glibc default collation can't mask a regression.

## Amendments (orchestrator review, 2026-10-12)

- **R1. The NULL ordering fix stays in the Postgres provider. A portable `CASE` key in shared code was rejected after checking what it costs SQLite.** Several SQLite composite indexes exist so that the database can return rows already sorted by a nullable key:
  - `(Type, TopParentId, StartDate)`;
  - `(…, IsVirtualItem, DateCreated)` in four variants (recently added);
  - `(Type, SeriesPresentationUniqueKey, ParentIndexNumber, IndexNumber)` (episode order);

  all in `BaseItemConfiguration.cs:52-74`. Putting `x == null ? 0 : 1` in front of those keys turns those index-ordered scans into temp-B-tree sorts on SQLite's hottest paths. MySQL and SQL Server already treat NULL as the lowest value, so Postgres is the outlier and the fix belongs in its provider.
- **R2. Keep the internal Npgsql surface small.**
  - Prefer building the stock `NpgsqlQuerySqlGenerator` with `reverseNullOrderingEnabled: true`. Fall back to overriding `VisitOrdering` only if the stock path emits wrong SQL, and record which one was used.
  - Put the dependency in one file. Suppress `EF1001` in that file only, with a one-line justification.
  - A `[PostgresFact]` asserts that the generated SQL contains `NULLS FIRST` for ascending and `NULLS LAST` for descending, so an Npgsql bump that breaks the generator fails CI rather than silently reordering results.
  - If upstream reviewers reject the internal API, the agreed fallback is to ask Npgsql to make `ReverseNullOrdering` public again. It is **not** the shared `CASE` rewrite.

## Amendments (multi-target re-check, 2026-10-12)

- **C1. NULL ordering stays a Postgres-only fix.** R1 is reconfirmed.
  - The probe shows that only PostgreSQL reverses NULL order compared with SQLite.
  - MySQL, MariaDB and SQL Server already sort NULL lowest, and they reject `NULLS FIRST`/`NULLS LAST` syntax anyway.
  - No shared change is made.
- **C2. Collation is set by a per-provider convention, at column level, like `"C"` on PostgreSQL.**
  - The collations:
    - MySQL: `utf8mb4_0900_bin`;
    - MariaDB: `utf8mb4_nopad_bin`;
    - SQL Server: `Latin1_General_100_BIN2` on `nvarchar`.
  - The probe confirms that each of these reproduces SQLite's code-point order for the test strings.
  - **Why the NO PAD variants:** plain `utf8mb4_bin` on MySQL and MariaDB is PAD SPACE. Under it, `Rock` and `Rock ` compare equal and collide in the unique `(Type, Value)` index, which SQLite accepts.
  - **New test (all engines):** seed `ItemValues` that differ only by trailing spaces. Both rows are stored, and an equality filter returns exactly one of them.
- **C3. SQL Server known gaps** (it is Experimental, ADR-0011):
  - **G1 (`ADR-0006/G1`):** `=` ignores trailing spaces under every collation (ANSI padding). Values that differ only by trailing spaces therefore compare equal and collide in unique indexes.
  - **G2 (`ADR-0006/G2`):** BIN2 on `nvarchar` orders by UTF-16 code unit. Supplementary characters (emoji, for example) sort before U+E000–U+FFFF, which SQLite does not do.
  - A UTF-8 `varchar` with `BIN2_UTF8` would fix G2. We did not choose it, because the length of a `varchar(n)` counts bytes, which breaks the `MaxLength`-in-characters bounds of ADR-0009 L1.

## Amendments (upstream reconciliation, 2026-10-12)

- **U1.** Nothing upstream addresses collation or NULL ordering. Watch #18299 (seeded random sort, `EF.Functions.Random`) and check it when the Postgres ordering translation is written in step 14.

## Amendments (step 14 gate, 2026-10-13)

- **R5. Null placement is written only where NULL can occur, and only nullable index columns get a null sort order. This refines the Decision's index rule and R2.**
  - Gate finding: PostgreSQL does not use NOT NULL to treat `NULLS FIRST` and `NULLS LAST` as the same order. Key and unique-constraint indexes cannot carry a null order and stay `ASC NULLS LAST`, so `ORDER BY "Id" NULLS FIRST LIMIT 10` lost its index-only scan and became a parallel sequential scan plus a sort on 200k rows. Paging by key (trickplay, devices) and every `Id` tie-breaker were affected. "Ordered index scans still apply" held only for non-constraint indexes.
  - Decision: the generator overrides `VisitOrdering` and appends `NULLS FIRST` (ascending) or `NULLS LAST` (descending) only when the expression can be NULL; a column that is not nullable in the query keeps the default clause-free ordering, and any expression whose nullability is not known keeps the clause. The index convention sets a null sort order only on nullable columns. Non-nullable columns sort the same either way, so results do not change.
  - R2 is revised accordingly: the override is now required, not a fallback. If it can be built on the public `NpgsqlQuerySqlGenerator` constructor with `reverseNullOrderingEnabled: false`, the internal builder method and the `EF1001` suppression go away. The generated-SQL test now also pins that a non-nullable column gets no clause.

## Amendments (step 24 spike, 2026-10-13)

- **C4. One collation for MySQL and MariaDB: `utf8mb4_0900_bin`.** This replaces C2's MariaDB entry. `utf8mb4_nopad_bin` exists only on MariaDB, and `utf8mb4_bin` is PAD SPACE on both, so C2 would need two migration sets. `utf8mb4_0900_bin` exists on both servers (MySQL 8.4.11, MariaDB 11.4.13), is NO PAD, and orders by code point. The spike's scripts generated on 8.4 and 11.4 are byte-identical and apply cleanly on both (`/tmp/s24-spike/evidence/00-mariadb-0900bin.txt`, `h-*`). The MariaDB floor is **11.4.7**: 11.4.4 lacks the collation, and 11.4.5 has it as an alias of the PAD SPACE `utf8mb4_bin` (MDEV-36361), so `Rock` and `Rock ` collide there. 11.4.7, 11.4.9 and 11.4.13 are NO PAD (`/tmp/s24-spike/evidence/f5-mariadb-collation-floor.txt`). Because an older server gives wrong results silently, the provider refuses to start below the floor. The C2 trailing-space test stays and runs on both servers.
- **C5. `LOWER()` folds Unicode under binary collations on both servers.** So the default `LikeIgnoreAsciiCase` translation is wrong there (`É`, the Kelvin sign and dotted I fold), and the provider must replace it with an ASCII-only fold (ADR-0005 R13). The chosen translation is in ADR-0005 R15.
- **C6. NULL ordering** on both servers matches SQLite, as C1 expected; no convention is needed.

## Amendments (step 24 gate, 2026-10-14)

- **C7. C2 is superseded for MySQL as well as MariaDB.** Both use `utf8mb4_0900_bin` (C4), set per column by the provider convention, and the connection character set is forced to `utf8mb4` (ADR-0011 F6), which that collation needs.

## Amendments (step 27a, 2026-10-15)

- **C8. On MySQL and MariaDB, every column stored as text is binary, chosen by its type in the database, and so is the default of the database and of every table.** This completes C4 and C7.
  - **What was wrong.**
    - The convention selected string properties by their .NET type (`Properties<string>().UseCollation`). `KeyframeData.KeyframeTicks`, an `ICollection<long>` stored as a JSON array in `longtext`, had no collation.
    - A column that names its character set and no collation gets the default collation of that character set, not the table's or the database's. Microting names the character set of every text column, so `KeyframeTicks` was `utf8mb4_0900_ai_ci` on MySQL and `utf8mb4_uca1400_ai_ci` on MariaDB.
    - The baseline's `AlterDatabase` ran `ALTER DATABASE CHARACTER SET utf8mb4`, which resets even a database created with `utf8mb4_0900_bin` to the server's default.
    - Microting's history repository names only the character set of `__EFMigrationsHistory`, so that table was case-insensitive in every database.
    - Probed in local runs on MySQL 8.4.11 and 9.7.2 and MariaDB 11.4.13 and 12.3.3 (`/tmp/27a-coll/probe-results.txt`). None of it changed a comparison Jellyfin makes, as `KeyframeTicks` is read back whole and `MigrationId` is compared as an exact ASCII id.
  - **The rule now.**
    - A model-finalizing convention (`TextCollationConvention`) gives `utf8mb4_0900_bin` to every property whose type in the database is `char`, `varchar` or one of the text types. It takes the type from the model, or from the provider's type mapping, after the conventions that change column types. So primitive collections, value-converted strings and JSON stored in text are covered.
    - A `json` store type is left out. In a local probe on MySQL 8.4.11 and MariaDB 11.4.13 (`/tmp/27a-coll/probe-json-results.txt`): on MySQL it has the binary character set and rejects a collation (error 1253); on MariaDB it is `longtext` with `utf8mb4_bin`. The model has none; one would need its own decision, and the real-server test would flag it on MariaDB.
    - The model names `utf8mb4` and `utf8mb4_0900_bin`, delegated to the database, the tables and the columns. So the baseline runs `ALTER DATABASE COLLATE utf8mb4_0900_bin`, every `CREATE TABLE` ends with `COLLATE=utf8mb4_0900_bin`, and a database that the provider creates itself gets both, as Microting's `CREATE DATABASE` takes them from the model.
    - The provider replaces the history repository with `BinaryCollationHistoryRepository`, which gives the history table and its columns the collation, as EF Core creates that table before the first migration runs.
  - **The baseline is regenerated, not followed by a new migration.**
    - The MySQL family is Experimental and fork-only (ADR-0011) and has never been released: no tag contains the baseline commit `d2fba4eee4`. ADR-0003 already allows a baseline to be regenerated until the first release that contains it.
    - A database migrated with the old baseline keeps its history row for the same id. EF Core does not run the baseline again, and the database keeps the old, harmless collations. No data-preserving upgrade path is owed. The fork branch is an unreleased development branch, so MySQL and MariaDB databases from earlier builds are recreated, not upgraded, as the setup pages say; no migration and no warning is added for them.
    - The regenerated baseline differs only in the collation of the database, of the 31 tables and of `KeyframeTicks`, and in a Down operation that resets the database default.
  - **Generated against an empty snapshot.** With no snapshot, EF Core 10.0.12 builds the first migration through `MigrationsModelDiffer.Add`, whose `DiffAnnotations(null, target)` never sets the collation of `AlterDatabase`. With an empty snapshot it uses `Diff(source, target)`, which does. `src/Jellyfin.Database/readme.md` gives the procedure, and an offline test fails if the first operation loses the collation.
  - **Contents check (22c).** Of the 10 baseline entries, only the reason of the MySQL `AlterDatabase` (Up) changed, to name the collation. One entry was added: the MySQL `AlterDatabase` in Down, `EngineSpecific`. The other nine are unchanged, because collations and annotations are not compared (`MigrationContents.cs:22-23`), and the regenerated baseline adds no other operation.
  - **Tests.**
    - **Offline.** Every property stored as text has the collation, on the provider-table rows MySQL 8.4 and 9.7 and MariaDB 11.4.7, 11.8 and 12.3. Every property stored as a string has a text type. The convention chooses by the type in the database, on a probe model with converted, collection and `json` columns. The baseline's first operation sets the collation. The baseline script gives the database, every table, the history table and every text column the collation.
    - **On MySQL and MariaDB.** A new database is migrated three ways: one the provider creates, one created with the server's defaults, and one created with the binary collation. `information_schema` must then report `utf8mb4_0900_bin` for the database, every table and every text column, the history table included. The text columns must be exactly those the model stores as text, plus the history table's two.
    - **At the base** `399af59770`, with only the two test files added, exactly 14 rows fail on each server, in local runs on MySQL 8.4.11 and MariaDB 11.4.13 (`/tmp/27a-coll/red`):
      - the five text-column rows, on `KeyframeTicks`;
      - the model default, the baseline's first operation, the two baseline-script rows and the history table;
      - the probe model;
      - the three real-server rows.
    - The guard that every property stored as a string has a text type passes there, as it should. The `IsText` unit test of the new convention cannot compile there.
  - **Also found at the base.**
    - Every table, not only the history table, defaulted to the server's case-insensitive collation, because the baseline named only the character set of each table. A column added later without a collation would have taken it.
    - Choosing by .NET type also gave the collation to a string property stored as `json`. MySQL rejects that when it creates the table. The probe model shows both.
