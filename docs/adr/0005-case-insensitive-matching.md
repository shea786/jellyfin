# ADR-0005: Case-insensitive matching and LIKE semantics

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

We verified this behaviour with EF 10.0.11 and PG 17.11/18.6:

| | SQLite | PostgreSQL |
|---|---|---|
| `LIKE` | case-insensitive for ASCII only (`'É' LIKE 'é'` = 0) | case-sensitive |
| default `LIKE` escape | none (`\` is literal) | `\` (`'AC\DC' LIKE '%AC\DC%'` = false) |
| `lower()`/`upper()` | ASCII only | follows the column collation: Unicode under libc/ICU, **ASCII only under `"C"`** |
| `string.StartsWith` | `LIKE @p ESCAPE '\'`, so **case-insensitive** | `LIKE @p`, case-sensitive |
| `string.Contains` | `instr()`, case-sensitive | `LIKE '%…%'`, case-sensitive |
| nondeterministic collation + `LIKE` | n/a | error on 17 (*"not supported for LIKE"*), works on 18 |

Sites that rely on SQLite's case-insensitive `LIKE`:

- `SqlSearchProvider.cs:125-134`. The comment at `:103-105` assumes `LIKE` ignores case.
- `BaseItemRepository.TranslateQuery.cs:268,273,489,496`. At `:489/495`, `CleanName` is compared with the **raw** `NameContains`.
- `ActivityManager.cs:80-108` (Name, Overview, ShortOverview, Type, Username).
- `PeopleRepository.cs:482`: `Name.StartsWith(lowercased input)`.

These columns are already normalised in app code, so comparisons against them are parity-safe:

- `CleanName` = `GetCleanValue()` (`StringExtensions.cs:159-176`: diacritics removed, `ToLowerInvariant`, punctuation turned into spaces);
- `SortName` (`BaseItem.cs:1004`, `ToLowerInvariant`);
- `ItemValue.CleanValue` (`ItemPersistenceService.cs:383`).

SQL `lower()`/`upper()` appears at `PeopleRepository.cs:55-60,182,477,487,492`, `QueryBuilding.cs:231-243`, `OrderMapper.cs:107-111` and the provider-ID filters `TranslateQuery.cs:1075-1090` (ASCII constants). All of them are parity-safe if PG's `lower()` is ASCII-only, which ADR-0006 guarantees with `"C"`.

## Options considered

- **A. `citext`.** It needs extension privileges, folds Unicode (so it differs from SQLite), and has no MySQL or SQL Server equivalent.
- **B. Nondeterministic ICU collation.** `LIKE` fails before PG 18, but the floor is 16 (ADR-0003). It would also make the unique `(Type, Value)` index (`ItemValuesConfiguration.cs:17`) reject `Rock`/`rock`, which SQLite accepts.
- **C. A DbFunction translated to `ILIKE` on PG.** Under `"C"` it is exactly SQLite's `LIKE`, but it is the dialect abstraction that ADR-0001 asks us to avoid.
- **D. Dialect-neutral rewrite:** `EF.Functions.Like(col.ToLower(), pattern, "\\")`, where the pattern is lower-cased for ASCII only and has `\` escaped in .NET. On SQLite the result is identical to today, because both `LIKE` and `lower()` fold ASCII only. On PG with `"C"` it is also identical. Verified on both engines: `ÉMILE` matches `Émile Zola`/`ÉMILE`, and `world` matches `hello World`.
- **E. Normalised columns maintained by the app** (for example `CleanOriginalTitle`). This gives Unicode-correct results on every engine, but needs a SQLite migration and a backfill of `BaseItems`.

## Decision

**D now, E as the target before MySQL or SQL Server ship.** Their `lower()` is Unicode even under binary collations, which breaks D's equivalence.

1. SQL never relies on implicit case-insensitivity. A case-insensitive match either compares an app-normalised column with an argument normalised the same way, or uses the shared helper `LikePatterns`. It lives in `Jellyfin.Database.Implementations` so that both `Emby.Server.Implementations` and `Jellyfin.Server.Implementations` can use it:
   - `LikePatterns.ContainsIgnoreAsciiCase(term)` returns `%{AsciiLower(EscapeBackslash(term))}%`.
   - It is used as `EF.Functions.Like(col.ToLower(), p, LikePatterns.Escape)`.
   - `%` and `_` typed by the user stay wildcards, as they are today.
2. Shared code never calls `StartsWith`/`EndsWith` on a mixed-case column. `PeopleRepository.cs:482` becomes `e.Name.ToLower().StartsWith(...)`.
3. `TranslateQuery.cs:489/495` compare `CleanName` with `nameContains.GetCleanValue()`.
4. `BannedSymbols.txt` bans the two-argument `EF.Functions.Like` in shared projects.
5. The PG baseline (ADR-0003) recreates `IX_Peoples_NameLower` as `lower("Name")`, mirroring `Providers.Sqlite/Migrations/20260728170000_AddPeopleNameLowerIndex.cs:16`. The index inherits `"C"`, so it matches `PeopleRepository.cs:55-60,182`. **`pg_trgm` is out of scope for v1**: `%term%` scans on both engines, as it does today. We will revisit with `EXPLAIN` on a library of 100k items.

## Consequences

- Non-ASCII case folding (`É`≠`é`) stays as it is on SQLite today, and becomes the same on PG. This is parity, not an improvement. ADR-0006's `"C"` collation is load-bearing.
- The SQL text changes for SQLite (a `lower()` wrapper and `ESCAPE '\'`), but results do not, and there is **no migration**. One deliberate change: `NameContains=Star` now matches `CleanName` `star wars`.
- Known SQLite bug, left unchanged: `PeopleRepository.cs:182` compares .NET Unicode-lowered names with SQL ASCII `lower()`, so `Émile` can be inserted twice. Fixing it is option E's job.

## Tests required

**(a) SQLite, in the portability PR:**

1. Search (`SqlSearchProvider` and `SearchTerm`): `OriginalTitle` `hello World` is found by `WORLD` and by `world`.
2. Search pins ASCII-only folding: `OriginalTitle` `Émile Zola` (with an unrelated `Name`) is found by `ÉMILE`, and **not** by `émile`.
3. A `\` in the term is literal: `AC\DC` matches `AC\DC` and not `ACDC`. A term containing `%` keeps today's wildcard behaviour.
4. `ActivityManager` filters (Name, Overview, ShortOverview, Type, Username) match mixed-case ASCII.
5. People: `NameStartsWith=bo` returns `Bob`. `NameContains`, `NameLessThan` and `NameStartsWithOrGreater` hold at mixed-case boundaries.
6. `NameContains=Star` matches `CleanName` `star wars` when `OriginalTitle` is null.
7. `/Persons` dedupe: `Bob`/`bob` collapse into one. `Émile`/`émile` are pinned as two.

**(b) Postgres:** 1-7 unchanged. Plus `[PostgresFact]` tests: `IX_Peoples_NameLower` exists, and `EXPLAIN` of the `:182` lookup uses it.

## Amendments (orchestrator review, 2026-10-12)

- **R1. Performance gate.** The Postgres provider-conventions PR (P8) is not done until a benchmark has run.
  - **Data.** A seeded library of **100k BaseItems and 300k People**.
  - **Measured.** The median and p95 of `SqlSearchProvider`, a `NameContains` item query, and a People prefix lookup.
  - **Comparisons.** (i) SQLite before and after the LIKE change (P5). (ii) SQLite against Postgres after P8.
  - **Thresholds.** P5 must not regress SQLite by more than 10%; if it does, the rewrite is reworked before PR. `pg_trgm` stays out of v1 unless Postgres is more than 3x slower than SQLite on the `%term%` queries.
  - **Recording.** The plans (`EXPLAIN QUERY PLAN` / `EXPLAIN ANALYZE`) and the numbers go into the PR explainer.

## Amendments (multi-target re-check, 2026-10-12)

- **E1. When option E lands.** It lands as `pr-plan.md` PR 20, at the start of group C0.
  - That is after PostgreSQL ships, and before any MySQL/MariaDB or SQL Server provider PR.
  - PostgreSQL ships on D, which is exact under `"C"`.
  - D is not exact on MySQL, MariaDB or SQL Server: their `LOWER()` folds Unicode even under binary collations, so ADR-0005 test 2 would fail there.
- **E2. Scope: the smallest set that removes SQL `lower()` on mixed-case columns from the shared queries.**
  - **`BaseItems.CleanOriginalTitle` = `GetCleanValue(OriginalTitle)`.** This is the same normaliser as `CleanName` (`StringExtensions.cs:159-176`). The search sites (`SqlSearchProvider.cs:125-134`, `TranslateQuery.cs:268,273`) compare it with the clean search term.
  - **`Peoples.NameLower` = `Name.ToLowerInvariant()`.**
    - It replaces SQL `lower(Name)` at `PeopleRepository.cs:55-60, 182, 477-492` and the `StartsWith` at `:482`.
    - A plain index on `NameLower` replaces the expression index `IX_Peoples_NameLower`. Expression indexes are not portable: MySQL has its own functional-index syntax, and SQL Server would need a computed column.
  - Both columns are maintained by the shared write paths (the mapper and the repository), not by a `SaveChanges` hook.
  - **ActivityLog stays on D** (`ActivityManager.cs:80-108`). Non-ASCII case folding in the admin activity-log filters is declared engine-defined, and the tests for those filters use ASCII only. Five new columns on a log table are not worth it for one admin filter.
- **E3. Cost.**
  - **SQLite migration:**
    - two `AddColumn` operations. Verified cheap on EF 10.0.11: a plain `ALTER TABLE … ADD`, with no table rebuild. (A change to collation or type *does* rebuild the table.)
    - one `CreateIndex`;
    - dropping the raw-SQL expression index.
  - **Backfill:**
    - a code routine, in batches, computes the values in .NET, because SQLite's `lower()` cannot.
    - It touches every `BaseItems` row with an `OriginalTitle` and every `Peoples` row: one full write pass over those rows, with progress logged.
  - **PostgreSQL:** a migration with the same ID.
- **E4. Deliberate behaviour changes on every engine, including SQLite.** Tests 2 and 7 flip in PR 20.
  - `émile` finds `Émile` in OriginalTitle search and in person lookups.
  - OriginalTitle search ignores diacritics, as `CleanName` search already does.
  - `/Persons` collapses `Émile`/`émile`, which fixes the known bug in Consequences.
- **E5. The LIKE escape on MySQL/MariaDB.**
  - Those engines need the literal written as `'\\'`. The provider's literal generator is responsible for that, and test 3 runs on every engine.
  - If a provider cannot do it, `LikePatterns.Escape` switches to a character that means nothing in any engine's literal syntax. That changes no SQLite result.
