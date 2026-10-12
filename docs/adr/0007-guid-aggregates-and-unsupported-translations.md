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
