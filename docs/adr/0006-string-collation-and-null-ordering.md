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
