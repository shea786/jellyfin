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

- **R1a. Benchmarks run under load, with plans as the gate (2026-10-13, user direction).** The machine is not going to be quiet, so the "quiet machine" requirement of R2 is replaced for every later benchmark (PostgreSQL in R1(ii), MySQL/MariaDB and SQL Server later).
  - **Equal budgets.** A database server under test runs in a container with explicit limits (`--cpus=2 --memory=4g` unless stated otherwise), and the SQLite side runs pinned to the same number of cores (`taskset`), so both get the same CPU budget.
  - **Interleaved runs.** The sides are run alternately in one window, at least three pairs (SQLite, other, SQLite, other, ...). Each side is judged on its lowest median and lowest p95 from that window.
  - **Context recorded.** The load average and the concurrent build or test activity at the start of each run go into the result file and the explainer.
  - **Plans decide.** A wrong plan blocks: a lost index, an unexpected sequential scan on a large table, an unexpected sort where an ordered index scan is expected (for example `NULLS FIRST` ordering). Timing thresholds (R1, R2) are indicative: a timing over a threshold with correct plans is recorded with its load context and does not block.

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

- **R6. Step 05 gate (PASS WITH NITS at `a5474e7943`) and API surface.**
  - `Jellyfin.Database.Implementations` is a published package on the ABI-compatibility list, so every public member is permanent plugin API. The step adds only `JellyfinDbFunctions.LikeIgnoreAsciiCase` and `LikePatterns` (`Escape`, `ContainsIgnoreAsciiCase`) as public. `ConfigureDefaults` is internal (tests through `InternalsVisibleTo`). The expression builder is not public: the SQLite provider builds its own `LikeExpression`. A plugin provider that wants the plain form writes the same few lines.
  - Doc comments claim correctness only "on engines whose `lower()` folds only ASCII letters", not on every engine. The SQLite comment says Jellyfin does not turn on `case_sensitive_like`; a user can still set it through the `#PRAGMA:` connection options, and then `LIKE` would no longer ignore case on SQLite. That is a user override, not supported behaviour.
  - Test cleanup folded in: the redundant People case-sensitive tests are reduced to the `NameStartsWith` theory, the SQL assertion no longer pins an alias or a parameter name, and a trivial constant test is gone.
  - **Follow-up, not in the series:** the `SearchTerm` wildcard branch in `BaseItemRepository.TranslateQuery.cs:264-269` cannot run. `GetCleanValue()` strips the wildcard characters before the check. It is pre-existing; step 05 only keeps it compiling with the escape argument.

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

## Amendments (PostgreSQL benchmark, 2026-10-13)

- **R7. R1(ii) result: plans pass; the timing threshold is tripped by JIT compilation, not by the scans.**
  - Run: commit `99615e9ca7`, protocol R1a. Three interleaved pairs, PostgreSQL 16.15 at `--cpus=2 --memory=4g`, SQLite and client pinned to two cores, load average 7-41 (recorded per run). Summary: `/tmp/s05-bench/R1ii-summary.txt`.
  - **Plans pass.** S9-S11 use `IX_BaseItems_SeriesName` as an index-only scan, forward and backward, with no sort and 0 heap fetches. S7 uses `IX_Peoples_NameLower` for the range scan and the anti-join probe. There is no unexpected sequential scan on an indexed predicate. Counts, set hashes and order hashes are identical across all six runs for S1-S14.
  - **Timings.** S7 runs at 0.8 times SQLite and S13 at 0.24 times. S1-S6 run at 8-34 times SQLite, which is over the 3x threshold that would call for `pg_trgm`. Of that time, 90-99 percent is JIT compilation; see ADR-0003 R4, which turns JIT off. `pg_trgm` is not added on this evidence. The 3x rule is checked again with JIT off before step 14 merges.
  - **Recorded, not blocking.** S13 uses an incremental sort over the `NULL` `SeriesName` group; an index on `(SeriesName, Id)` would stream, which is optional. S14's `SELECT DISTINCT` over all columns spills about 10 MB at `work_mem` 4MB. That query shape is the same on every engine (SQLite builds a temp B-tree), so it is a follow-up outside this series.
- **R8. JIT-off re-run confirms the cause (`/tmp/s05-bench/R1ii-jitoff-summary.txt`).** Same commit and protocol, three further interleaved pairs, load average 20-46, JIT turned off on the server only. S1-S6 now run at 0.69-1.12 times SQLite on the lowest median (worst single pair 2.01), so the 3x rule passes and `pg_trgm` is not added. S1 drops from 251 ms to 34 ms, S3 from 623 ms to 24 ms, S6 from 1330 ms to 30 ms. Plans are unchanged: S9 is still an index-only scan with 0 heap fetches and no sort; S12 and S14 get faster; nothing got worse. Counts and set and order hashes match across all 13 result files. The remaining ratios above 1 are S9-S11 (1.6-2.5 times, 0.1-1.7 ms absolute).

## Amendments (step 20 gate, 2026-10-13)

- **R9. What option E changed, as built.** These replace the step 05 rules in R3 where they differ, on every engine including SQLite.
  - **OriginalTitle** is matched through `CleanOriginalTitle`, so case, diacritics and punctuation are ignored, as for `CleanName`. `Spider-Man` and `spider man` match each other; `spiderman` does not.
  - **Test 3 changes meaning.** `\` is punctuation to `GetCleanValue()`, so `AC\DC` and `ac dc` both find `AC\DC`, and `ACDC` does not. The test now pins that. `%` and `_` typed by the user stay wildcards, as before.
  - **A term with nothing to look for** (no letter, digit, `%` or `_` once punctuation is removed, such as `-`, `...`, `\` or only spaces) matches no item, in `SearchTerm`, `NameContains` and the search provider alike. R3's rule, that such a `NameContains` term matched OriginalTitle only, is withdrawn: with the cleaned column it would have matched every title. `SearchTerm` used to match every item for such a term; matching everything for a term with nothing in it was judged wrong, so this is a visible change. A wildcard term that cleans to nothing (only `[ ] ^` plus a wildcard) still matches on `CleanName`.
  - **`/Persons`** collapses names that differ only in case, including non-ASCII (`Émile`/`émile`). Tests 2 and 7 are flipped, not deleted.
  - **R3 gap 1 is closed**: `ÉMILE`, `émile` and `emile` all find `Émile Zola`.
  - **R3 gap 2 (SortName) is not closed by step 20.** A non-ASCII upper-case `SortName` that differs from `Name` is still not found by a lowered term. It is narrow today: a person's sort name is its name, which `CleanName` covers, and alphanumeric sorting already stores it lower-cased. But it must be closed before an engine whose `lower()` folds Unicode ships, or that engine would return different results from SQLite. It becomes step 20b (`pr-plan.md`), an entry criterion for step 24.
- **R10. Escape argument.** The two-argument `EF.Functions.Like` must not return in files that disable RS0030 for the whole file (decision 4 relies on review there). Step 20's gate found six such calls; they now pass `LikePatterns.Escape`.
- **R11. Plans after step 20.** SQLite's People prefix lookup is an ordered scan of the plain `IX_Peoples_NameLower` (no range seek, as with the old expression index, because `LIKE` is case-insensitive there); 11-14 ms at 300k people, the same as at the base. PostgreSQL 16 range-scans the btree under `"C"` (`>= 'bo' AND < 'bp'`), including for a non-ASCII prefix.
- **R12. Backfill window.** The backfill routine runs during application initialisation, after the EF migrations and before the web host starts, and is recorded only when it completes. An interrupted run repeats on the next start, so the empty default of `NameLower` is never visible through the API.
- **R13. Step 20b is narrowed: SortName stays as stored (design check, 2026-10-13).** R9's premise was wrong. Mixed-case `SortName` is the common case (episodes, songs, named seasons and channels keep the raw name; people only lose leading spaces; an NFO `<sortname>` is stored as written). Storing it lower-cased would change `BaseItemDto.SortName` for most of a library and make the default order case-insensitive. Both changes are visible to clients, and parity does not need them, so they are out of this series. A case-insensitive order can be offered upstream as a separate change.
  - **What parity does need.** Every case-insensitive comparison of `SortName` must go through the ASCII-only fold contract of `JellyfinDbFunctions` (the default translation is exact where `lower()` folds ASCII only; a provider replaces it otherwise). The search provider already does this (`SqlSearchProvider.cs:132`), and its term is transliterated to ASCII first. The `NameStartsWith` filter does not: it uses `SortName.ToLower().StartsWith(term.ToLowerInvariant())` (`BaseItemRepository.QueryBuilding.cs:232`), and `ToLower()` becomes the engine's own `LOWER()`, which folds Unicode on MySQL, MariaDB and SQL Server.
  - **Step 20b.** `NameStartsWith` moves to `LikeIgnoreAsciiCase` with an escaped prefix pattern. On SQLite this makes one small visible change: an exact-case non-ASCII prefix (`É` against `Émile`) now matches, while a different-case one (`é`) still does not. The commit must say so. The range filters `NameStartsWithOrGreater` and `NameLessThan` (`:239`, `:246`) keep `ToLower()`. The alphabet picker sends ASCII letters, and with an ASCII term the engines differ only for stored characters whose Unicode lower case is ASCII (the Kelvin sign, dotted capital I). A non-ASCII term is a recorded known gap on MySQL/MariaDB and SQL Server, not a new public function. Parity tests cover both filters with ASCII terms and pin the gap with a ledger entry.
  - **Provider obligation, an entry criterion for steps 24 and 27.** The MySQL/MariaDB and SQL Server providers must translate `LikeIgnoreAsciiCase` so that it folds ASCII letters only. It must not rely on a `_ci` collation (ADR-0006 C2 uses binary collations) or on `LOWER()`, which folds Unicode on those engines. `LikeIgnoreAsciiCaseTranslationTests` gains cases that must hold on every pooled backend: `É` vs `é`, the Kelvin sign, dotted capital I, `ß`, and an ASCII term against mixed-case ASCII.
  - **No routine and no restore handling** are needed, because no stored value changes.
