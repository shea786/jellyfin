# ADR-0007: Aggregates over Guid and other provider-sensitive translations

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- Presentation-key grouping on the item-list hot path selects a representative with `MIN` over `Id`:
  - `BaseItemRepository.QueryBuilding.cs:101-115`, at `:103,109` (`Min((Guid?)Id)` with a `PrimaryVersionId == null` preference) and at `:114`;
  - `BaseItemRepository.ByName.cs:257-260`.
- Npgsql translates these to `min(uuid)`. That function **does not exist on PG 17.11 or 18.6** (verified, `42883`).
- **Ordering equivalence.** SQLite stores a Guid as canonical upper-case `TEXT`. PG compares `uuid` bytes in canonical order. Hex digits sort the same way in both cases, so the smallest Guid is the same row on both engines. Verified: `OrderBy(Id)` gave identical results.
- **Sweep of other provider-sensitive LINQ** in the shared query code:
  - Guid `<` (`PeopleRepository.cs:55`) translates on both engines (verified).
  - `EF.Functions.Random()` (`OrderMapper.cs:55`) becomes `random()` on both, and is non-deterministic anyway.
  - Every `Math.*` call runs on the client (`TrickplayManager`, `StorageHelper`).
  - There is no `string.Compare`. String `CompareTo` and string `MIN` belong to ADR-0006, LIKE/`lower` to ADR-0005, and `DateTime` arithmetic to ADR-0004.
  - One inconsistency that is not a parity issue: `ByName.cs:247-252` sorts with .NET `Guid` order in memory, which differs from database order, so the two `ByName` branches may pick different representatives. We record it and leave it alone.

## Options considered

- **A. Neutral LINQ rewrite:** `g.OrderBy(e => e.PrimaryVersionId == null ? 0 : 1).ThenBy(e => e.Id).Select(e => e.Id).First()`. Verified: it translates to a correlated `LIMIT 1` subquery per group, with identical results on both engines. But it replaces one hash aggregate on SQLite's hottest query with N correlated lookups, and needs benchmarking on large libraries.
- **B. `MIN(CAST(Id AS TEXT))`** and a conversion back: awkward, needs `Guid.ToString()` translation, and defeats indexes.
- **C. Provider-owned aggregates** `min(uuid)`/`max(uuid)` in the PG baseline migration:
  - built from the function `jf_uuid_smaller`/`jf_uuid_larger` (`LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE`) and `CREATE AGGREGATE … (SFUNC, STYPE = uuid, COMBINEFUNC, SORTOP = <|>, PARALLEL = SAFE)`;
  - measured on PG 17 with 500k rows and 20k groups: 29 ms, against 24 ms for `min(uuid::text)`.
- **D. `ROW_NUMBER()` window:** EF Core 10 LINQ cannot express it.

## Decision

**C.** Shared code and SQLite stay unchanged.

- The aggregates are schema objects owned by the provider (ADR-0001 dialect policy, step 2).
- This **replaces the "Guid MIN rewrite" example in ADR-0001 and pr-plan scope 01**. We rejected the rewrite because of the hot-path risk.
- The migration is guarded: it creates the aggregate only when `pg_aggregate` has no `min(uuid)`, so a future PG that ships one natively keeps its own. `Down` drops only the `jf_`-prefixed objects and the aggregates built on them.
- The aggregates are ordinary schema objects, so `pg_dump`/restore carries them.

## Consequences

- The PG baseline (ADR-0003) gains one raw-SQL block. The migration tests from ADR-0002 cover it.
- MySQL can store Guids as `char(36)`, where native `MIN` is text order, the same as SQLite.
- SQL Server has `MIN(uniqueidentifier)`, but its order puts the last 6 bytes first, so it would pick a different representative. The SQL Server ADR must solve this, and the parity tests below will fail until it does.
- If profiling later shows that the correlated rewrite (A) is no slower on SQLite, we can revisit. The tests stay as they are.

## Tests required

**(a) SQLite (pin-only PR):**

1. `GroupByPresentationUniqueKey_PrefersPrimaryVersionThenLowestId`. Seed three items with one key: an alternate version (`PrimaryVersionId` set) with the lowest Id, plus two primaries. Expect the primary with the lower Id. Choose Ids whose database order differs from .NET `Guid.CompareTo` order (for example `0fffffff-…-000000000000` against `10000000-…-0000000000ff`), so the test pins database order.
2. The same for the `GroupBySeriesPresentationUniqueKey` paths (`:103` and `:114`).
3. The `ByName` representative (`ByName.cs:259`) is the lowest Id, with no `TopParentIds`.
4. Each query runs fully in SQL, with no client-evaluation warning or exception.

**(b) Postgres:**

- 1-4 unchanged;
- `[PostgresFact]` migration tests: `min(uuid)` exists after `Migrate()`;
- running `Up` against a database that already has a `min(uuid)` aggregate is a no-op;
- `Down` removes only the `jf_` objects.

## Amendments (orchestrator review, 2026-10-12)

- **R1. Create only what is used.**
  - Shared code uses `MIN` over Guid only. No `Max` over a Guid key exists in the repositories.
  - The baseline therefore creates only `jf_uuid_smaller` and `min(uuid)`.
  - A `max(uuid)` is added only alongside the first code that needs it.
- **R2. Policy for provider-owned schema objects. It applies to every such object on any provider.**
  - **Location and privileges.** Objects are created by the migration, in the same schema as the tables, without schema qualification. They are resolved through `search_path` exactly as the unqualified table names are. `LANGUAGE sql` is trusted, so the only privilege needed is `CREATE` on the schema, the same as for creating tables. No superuser and no extension are required.
  - **Backup and restore.**
    - `pg_dump`/`pg_restore` carry the objects as ordinary schema objects.
    - The JSON `BackupService` path restores into a database that the migrations have already built, so the objects already exist.
    - `PurgeDatabase` (`TRUNCATE`) does not touch them.
  - **When Postgres ships its own `min(uuid)`.** Postgres resolves `pg_catalog` before the schemas in `search_path`, so a built-in `min(uuid)` wins automatically and ours becomes unused but harmless.
    - Once the floor is a version that has the built-in aggregate, a guarded migration drops ours.
    - The creation guard (skip if `min(uuid)` already resolves) covers fresh installs on such versions.
- **R3. Additional tests (`[PostgresFact]`).**
  - A JSON backup/restore round trip into a freshly migrated database, followed by the grouping query.
  - After restoring a `pg_dump` into an empty database, the grouping query still runs.

## Amendments (multi-target re-check, 2026-10-12)

- **G1. Correction: SQL Server has no `MIN` over `uniqueidentifier`.** The probe on SQL Server 2022 CU27 got an error. Its `uniqueidentifier` order also differs from every other engine. The Consequences bullet that says otherwise is wrong.
- **G2. Guid storage is chosen per engine so that byte order equals canonical text order,** which is SQLite's order. Native `MIN` then works everywhere except on PostgreSQL. Shared code and SQLite do not change.
  - **PostgreSQL:** `uuid` with the `jf_uuid_smaller`/`min(uuid)` aggregate, unchanged. This item lives in the PostgreSQL baseline (`pr-plan.md` PR 15). It is not a shared rewrite.
  - **MySQL/MariaDB:** `binary(16)` in RFC 4122 big-endian byte order, using the provider's Guid-format option. MariaDB's native `UUID` type is not used, because we have not verified its comparison order.
  - **SQL Server:** `binary(16)` through a provider converter, `Guid.ToByteArray(bigEndian: true)`.
    - Verified on EF 10.0.11: the grouping query translates to `COALESCE(MIN(CASE … [Id] END), MIN([Id]))` over `binary(16)`.
    - `uniqueidentifier` was rejected. Both `MIN` and every Guid comparison (for example `PeopleRepository.cs:55`, `other.Id < p.Id`) would follow a different order.
    - The cost: `binary(16)` is harder to read in SQL tools. Key size is unchanged at 16 bytes.
  - **CockroachDB** has a native `min(uuid)`, but CockroachDB is rejected.
- **G3. Tests.**
  - Tests 1-4 run unchanged on every engine. Test 1 deliberately uses Ids whose .NET order differs from their canonical order, so a wrongly configured Guid format fails it.
  - A model test per engine asserts the store type of every Guid column.

## Amendments (PR 01 findings, 2026-10-12)

- **P1. The premise of test 1 is corrected.** On SQLite, database order does **not** differ from .NET `Guid.CompareTo` order.
  - SQLite stores canonical upper-case text, and .NET compares the fields unsigned, field by field. Both give canonical order.
  - The order that differs is the memcmp order of the little-endian `Guid.ToByteArray()` bytes. The Ids in test 1 (`0fffffff-…-000000000000` vs `10000000-…-0000000000ff`) still serve their purpose, because they are ordered differently under little-endian bytes.
  - So test 1 does not catch a SQLite problem. It catches a provider that stores Guids as **little-endian** `binary(16)`, which is the default of EF's `GuidToBytesConverter` and of Pomelo/Microting's `LittleEndianBinary16`.
- **P2. Each Guid format choice in G2 must be checked when its provider lands.**
  - MySQL/MariaDB: confirm that the chosen `MySqlGuidFormat` writes RFC 4122 big-endian bytes.
  - SQL Server: the converter must call `ToByteArray(bigEndian: true)` and `new Guid(bytes, bigEndian: true)`.
  - Test 1 is the acceptance check in both cases.
- **P3. Test a4 (no client evaluation) is dropped.** EF Core already throws when a predicate, ordering or grouping cannot be translated. Only the final projection may run on the client, and it does not decide which representative is chosen. A test for a4 would only re-test EF.
- **P4. Coverage gap noted, not fixed.** The `GroupBySeriesPresentationUniqueKey` + presentation-key branch of `ApplyGroupingFilter` cannot be reached through the public repository API, so PR 01 covers only the plain `Min(Id)` path. Whether that branch is dead code is for a separate PR.
- **P5. Non-ASCII uppercase range bounds are left out of PR 01.** Today they would pin a SQLite-only result (ASCII-only `lower()`). PR 20 adds them when it changes case folding (ADR-0005 E4).
- **P6. Corrections from the PR 01 review.**
  - **The `ByName.cs:247-252` inconsistency in Context is withdrawn.** That branch picks its representative in memory with `ThenBy(e => e.Id)`. .NET `Guid.CompareTo` follows canonical order, so both `ByName` branches agree. That branch pins no database behaviour, so no test targets it.
  - **Test Ids must invert canonical order under both byte orders that cause trouble.** The pair to use is `0fffffff-0000-0000-0000-0000000000ff` (canonically lower) and `10000000-0000-0000-0000-000000000000`. It sorts the opposite way under little-endian `ToByteArray()` memcmp **and** under SQL Server `uniqueidentifier` order, which compares the last 6 bytes first. The earlier pair inverted only the little-endian order, so it would not have caught a SQL Server provider that keeps native `uniqueidentifier` (G2).

## Amendments (upstream reconciliation, 2026-10-12)

- **U1.** Nothing upstream addresses Guid `MIN`/`MAX`.
- **U2. `WhereOneOrMany` is untested on Postgres.** `JellyfinQueryHelperExtensions.WhereOneOrMany` builds `Enumerable.Contains` over `EF.Parameter`. EF translates it per provider: `json_each` on SQLite, `= ANY(@p)` on Postgres. That should be portable, but nothing tests it. Step 14 adds a Postgres query test covering lists of zero, one and many values.
