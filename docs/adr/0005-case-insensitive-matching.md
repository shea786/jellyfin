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

- **R2. Benchmark protocol for step 05 (from the baseline at `51c81ac7ee`).** Tooling lives in `/tmp/s05-bench` (`run.sh <commit>`, `compare.py`); the seed is 100k items, 300k people, about 600k people maps and 10k activity rows, migrated through the real EF migrations.
  - **Quiet machine.** The baseline ran with a load average of 11-21 on 20 cores from parallel builds, and one 3 ms query doubled from load alone. The comparison runs are made when no other build or test job is running.
  - **Interleaved runs.** Before and after are run alternately in one window (before, after, before, after). Each metric is compared on the lowest median and the lowest p95 per side.
  - **Threshold.** For a query whose baseline median is 10 ms or more, the 10% rule applies. Below 10 ms (the ActivityManager filter, about 3 ms), the rule is an absolute bound: flag a difference over 1 ms. Ten percent of 3 ms is inside the noise.
  - **Result sets.** Counts must be identical except where step 05 changes behaviour on purpose: `NameContains` with mixed case now matches as the lower-case term does (so the mixed-case query returns the same items as the lower-case one), and `NameContains` with a diacritic now matches through `CleanName`. Each changed count is checked against the intended behaviour, not flagged as a regression.
  - **Plans.** Query plans are compared, not only timings. The People prefix lookup must not lose an index it uses today (`IX_Peoples_NameLower` or another).
  - **Limits, stated in the explainer.** Search runs with no user, so no access filtering; code migrations are not applied to the seed.

- **R3. Step 05 rulings (worker report, tip `f93a6b4aae`).**
  - **The ban has two blind files.** `SqlSearchProvider.cs` and `BaseItemRepository.TranslateQuery.cs` already disable RS0030 for the whole file, for the banned `Guid` operators. The ban cannot catch a two-argument `Like` there. Accepted: narrowing those pragmas is an unrelated change to upstream files. Every other file is covered, the step leaves no two-argument `Like` anywhere, and the commit body states the gap.
  - **New file-level CA1304/CA1311 suppressions** in `SqlSearchProvider.cs` and `ActivityManager.cs`, for `ToLower()` inside EF expressions. Accepted: `PeopleRepository.cs` and `BaseItemRepository.TranslateQuery.cs` already do the same, and the culture overloads do not translate to SQL.
  - **`NameContains` with a term that cleans to empty** (such as `-`) matches `OriginalTitle` only. Without the guard, `CleanName.Contains("")` would match every row. This keeps today's result for such terms. It is a behaviour decision and goes in the explainer.
  - **The wildcard branches** use the ASCII-lowered pattern for both `CleanName` and `OriginalTitle`. `CleanName` is already lower-case, so this only makes an upper-case wildcard term match. Explainer.
  - **Remaining gaps, unchanged and stated in the explainer:** `SqlSearchProvider` strips diacritics from the term, so `ÉMILE`/`émile` find nothing there (pre-existing). A non-ASCII upper-case `SortName` does not match a lowered term, because SQLite's `lower()` folds ASCII only. Step 20 (option E) fixes both.

- **R4. Step 05 failed the R1 gate on D; option C' replaces D in shared code.**
  - **Evidence.** On the quiet benchmark (R2), D cost +27% to +51% on the search and `NameContains` queries and +2 ms on the activity filter. The plans were unchanged; the cost is the per-row `lower()` on `OriginalTitle` and `SortName`. A rework was required.
  - **C'.** Shared queries call a marker function, `JellyfinDbFunctions.LikeIgnoreAsciiCase(value, pattern)`, with a pattern from `LikePatterns`. `JellyfinDbContext.OnModelCreating` registers the default translation, `lower(value) LIKE pattern ESCAPE '\'` (exactly D), before the provider's `OnModelCreating`, so a provider can replace it. The SQLite provider replaces it with `value LIKE pattern ESCAPE '\'`. That is exact on SQLite, because its `LIKE` and `lower()` both fold ASCII only and Jellyfin never sets `case_sensitive_like`. Only public EF APIs (`HasDbFunction`, `HasTranslation`, `LikeExpression`); no snapshot change, no migration.
  - **Why this is not the C rejected above.** C was a provider-specific SQL dialect in shared code. Here shared code states one intent, the shared default is correct on every ASCII-folding engine (SQLite, PostgreSQL under `"C"`), and plugin providers get the default without doing anything. PostgreSQL ships on the default; `ILIKE` would need Npgsql internals.
  - **Growth rule.** Each new `JellyfinDbFunctions` member names one operation, has a shared default that is correct on every engine, and is justified by a measured cost. It must not become a dialect service.
  - **Kept from D.** People `NameStartsWith` keeps `Name.ToLower().StartsWith(...)`: on SQLite it scans the covering index `IX_Peoples_NameLower` and is about 12 times faster than before (336 ms to 26 ms).
  - **Prototype numbers** (`scratch/s05-arch`, `7c2153f805`, load 5.7-9.2): S1-S6 within -2.4% to +8.6%, S8 +0.16 ms, S7 25.6 ms. S5's p95 reached +10.0% against one unusually low baseline; the final branch is benchmarked again on a quiet machine. The cost of matching the cleaned term (`instr(CleanName, 'ecole')`, the intended `NameContains` fix) is part of the behaviour change, but it is still measured against the gate.
  - **Tests.** The prototype ran the default translation on SQLite with `case_sensitive_like` on, through a provider-neutral model, a model-cache-key factory and a second fixture constructor. That machinery is dropped: it overlaps the test harness, which reorganises the fixtures. Instead:
    - unit tests read the registered translations from a model and check their shape: the default is `lower(value) LIKE pattern ESCAPE '\'`, the SQLite provider's replaces it when it runs after the default, and calling the function outside a query throws;
    - a SQLite test checks that `x LIKE p ESCAPE '\'` and `lower(x) LIKE p ESCAPE '\'` agree over a grid of values and patterns;
    - the behaviour of the default on a case-sensitive `LIKE` is covered for real when the same tests run on PostgreSQL (the harness rebase onto step 05 must show them passing there).
  - **Checklists.** The `LikeExpression` is built with a null type mapping. It works in `WHERE` on SQLite; its use in a projection, on PostgreSQL (step 14) and on SQL Server (step 27) is checked there.

- **R5. Final step 05 benchmark and the S5 ruling (tip `a5474e7943`, same tree as `39c720353f`).** Quiet machine (load 2.0-6.7), interleaved before/after/before/after, lowest median and lowest p95 per side, ms:

  | Query | Median before -> after | p95 before -> after | Rows |
  |---|---|---|---|
  | S1 search | 30.09 -> 31.02 (+3.1%) | 31.26 -> 32.27 (+3.2%) | same |
  | S2 search, no match | 29.17 -> 30.01 (+2.9%) | 30.13 -> 31.30 (+3.9%) | same |
  | S3 NameContains | 22.86 -> 23.39 (+2.3%) | 23.63 -> 24.49 (+3.7%) | same |
  | S4 NameContains, mixed case | 21.88 -> 22.96 (+4.9%) | 22.97 -> 24.24 (+5.5%) | 25 -> 36, intended (equals S3) |
  | S5 NameContains `école` | 23.53 -> 25.71 (+9.2%) | 24.68 -> 27.15 (+10.0%) | same |
  | S6 SearchTerm | 22.41 -> 22.69 (+1.2%) | 23.36 -> 24.05 (+2.9%) | same |
  | S7 People prefix | 324.80 -> 25.72 (-92%) | 330.67 -> 27.29 | same |
  | S8 activity filter | 2.96 -> 3.17 (+0.21 ms) | 3.07 -> 3.31 | same |

  - Plans are unchanged except S7, which now scans the covering index `IX_Peoples_NameLower`.
  - **S5 is accepted, on its measured cause.** Its p95 lands on the 10% line (+10.008%), consistently across four after-runs. The SQL differs from before in two places only: `ESCAPE '\'` (no measurable cost) and the `instr` needle, `ecole` instead of `école`. That needle is the deliberate `NameContains` fix: `CleanName` has no diacritics, so `école` could never match it. On the seed, `instr(CleanName, 'ecole')` costs about 1 ms more per 100k rows than `instr(CleanName, 'école')`, because a needle starting with a common byte (`e`) takes more partial comparisons than one starting with `0xC3`. The old code's S5 was cheap only because its `CleanName` check could never succeed. The cost is the ordinary cost of searching for a term starting with `e`, not a cost of the LIKE rewrite, which is within +1% to +5% everywhere else.
  - The commit messages state the D measurement as "27 to 51 percent slower", the figure measured.

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

## Amendments (upstream reconciliation, 2026-10-12)

- **U1. Precedent for option E.** Upstream #16919 added `NormalizedUsername`, a column the application normalises and stores. That is the pattern option E uses, so we cite it as precedent.
- **U2. Call sites, updated.**
  - #18223 added another implicit-case `LIKE` at `SqlSearchProvider.cs:134` (the `SortName` branch). It joins the step 05 list.
  - #18265 fixed `PeopleRepository.cs:487` and `:492` (`CompareTo`), but not the `StartsWith` at `:482`, which lowers only the argument. That one stays ours in step 05.
- **U3. Watch #16301.** It adds a SQLite-only `CleanValue` user-defined function for non-ASCII search. It is not portable and conflicts with option E in `TranslateQuery.cs`. If it moves forward, the user may want to raise option E in the upstream discussion.
