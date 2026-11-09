# Explainer: step 05, case-insensitive matching that does not depend on the engine

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text. The whole feature goes upstream as one PR (`pr-plan.md` S1), so this step is six commits in that series.

- **Branch:** `multidb/05-case-insensitive-matching`, six commits on top of step 04 (`51c81ac7ee`).
  1. `4829abb754` Pin the current case-insensitive matching on SQLite (tests only, +465)
  2. `0ddc32b2f8` Add a database function for LIKE that ignores ASCII case (+309)
  3. `b167c1158f` Match search and activity filters with LikeIgnoreAsciiCase (+15/-16)
  4. `01ab65b1d0` Lower the person name in the NameStartsWith filter (+58/-1)
  5. `676c1bf171` Compare the clean name in NameContains with its clean value (+133/-6)
  6. `ccbb7d666e` Ban the two-argument EF.Functions.Like (+1)
- **Size:** 19 files, +981/-23. Production code is 10 files, +143/-23; the rest is tests.
- **Status:** gated and merged into `feature/multi-db`. Not pushed.

## What the step is, in one sentence

Text filters that relied on SQLite's `LIKE` ignoring case now say so explicitly, so they find the same rows on an engine whose `LIKE` is case-sensitive, while SQLite keeps the SQL it had and its speed.

## The problem

| | SQLite | PostgreSQL |
|---|---|---|
| `LIKE` | ignores case, ASCII letters only | case-sensitive |
| default `LIKE` escape | none, `\` is literal | `\` |
| `lower()` | ASCII only | ASCII only under the `"C"` collation the PG provider will use |

Search (`SqlSearchProvider`, the `SearchTerm` and `NameContains` filters) and the activity log filters passed the user's term straight to `EF.Functions.Like`. On SQLite `WORLD` finds `hello World`; on PostgreSQL it does not, and a `\` in the term would act as an escape.

## The design, and why it changed during the step

**First version (option D, rejected by measurement).** `EF.Functions.Like(column.ToLower(), pattern, "\\")`, with the pattern lowered for ASCII in .NET. Identical results on SQLite. But the per-row `lower()` made SQLite search 27 to 51 percent slower on a 100,000-item library (ADR-0005 R4). Our own gate (at most 10 percent slower on SQLite) failed it.

**Final version (C').**

- Shared code calls a marker function, `JellyfinDbFunctions.LikeIgnoreAsciiCase(column, pattern)`. It throws if run in .NET; it only exists to be translated.
- `LikePatterns.ContainsIgnoreAsciiCase(term)` builds the pattern: `%`, the term with A-Z lowered and `\` doubled, `%`. A `%` or `_` the user typed stays a wildcard, as today.
- `JellyfinDbContext.OnModelCreating` registers the **default translation**, `lower(column) LIKE pattern ESCAPE '\'`, before the provider's `OnModelCreating`. Exact wherever `lower()` folds only ASCII. A plugin provider that does nothing gets it.
- The **SQLite provider** replaces it with `column LIKE pattern ESCAPE '\'`. On SQLite `LIKE` and `lower()` both fold ASCII only, so the two forms return the same rows; a grid test checks it. Jellyfin does not turn on `case_sensitive_like`. A user could, through the `#PRAGMA:` connection options; then `LIKE` stops ignoring case. That is a user override, not supported behaviour.
- Only public EF Core APIs (`HasDbFunction`, `HasTranslation`, `LikeExpression`). No model snapshot change, no migration.

**Why this is not the "dialect abstraction" ADR-0005 rejected.** Shared code states one intent ("match ignoring ASCII case"), not SQL. The default is correct on every engine we target first. A provider may only replace it with something that gives the same rows. The ADR adds a rule so this does not grow into a dialect service: each member names one operation, has a correct shared default, and is justified by a measured cost.

**Public API.** `Jellyfin.Database.Implementations` is a published package that plugins compile against, so public members are permanent. Public: `LikeIgnoreAsciiCase`, `LikePatterns.Escape`, `LikePatterns.ContainsIgnoreAsciiCase`. `ConfigureDefaults` is internal (a new `Properties/AssemblyInfo.cs` gives the test project access; the csproj route does not work there because `GenerateAssemblyInfo` is off). The SQLite provider builds its own `LikeExpression` rather than using a shared public builder.

## Commit by commit

1. **Pin today's behaviour (tests only).** They pass on the base and on every later commit unchanged: mixed-case ASCII matching in search, `SearchTerm`, the five activity filters and People; `ÉMILE` finds `Émile Zola` but `émile` does not (ASCII-only folding, pinned, not endorsed); `AC\DC` is literal; `%` keeps its wildcard meaning; `Bob`/`bob` collapse in `/Persons` while `Émile`/`émile` stay two (an existing limitation, pinned).
2. **The function and its translations.** Tests check the shape of both translations from a model, that the real context ends up with the SQLite one (through `ToQueryString`), that calling it outside a query throws, and the SQLite equivalence grid. Nothing calls it yet.
3. **Switch the call sites.** `SqlSearchProvider` (`OriginalTitle`, and `SortName` from upstream #18223), `SearchTerm`, and the activity filters. On SQLite the SQL keeps its shape, `column LIKE pattern`, now with `ESCAPE '\'` and a lowered pattern. Commit 1's tests pass unchanged.
4. **People `NameStartsWith`.** It lowered only the argument: `Name.StartsWith(term.ToLowerInvariant())`, case-insensitive on SQLite by accident (EF translates `StartsWith` to `LIKE`). Now `Name.ToLower().StartsWith(...)`, like the two filters next to it (fixed upstream in #18265, which missed this one). Side effect on SQLite: the query now scans the covering index `IX_Peoples_NameLower` and got about 12 times faster (325 ms to 26 ms on 300,000 people). This one keeps `ToLower()` rather than the new function because the index is on `lower("Name")`.
5. **`NameContains` compares `CleanName` with the cleaned term.** A real bug fix and a deliberate behaviour change: `CleanName` is stored lower-case without diacritics or punctuation, but was compared with the raw term, so `Star` never found `star wars` through `CleanName`. A term that cleans to nothing (such as `-`) looks only in `OriginalTitle`, because `Contains("")` would match every row; that keeps today's result for such terms. The wildcard branch now lowers the pattern for both columns, so an upper-case wildcard term matches.
6. **Ban the two-argument `EF.Functions.Like`** in `BannedSymbols.txt`, so new code cannot quietly depend on the engine again. Proven with a temporary RS0030 build failure. Blind spot, stated in the message: `SqlSearchProvider.cs` and `BaseItemRepository.TranslateQuery.cs` already disable RS0030 for the whole file (for the banned `Guid` operators), so the ban cannot see a new two-argument `Like` there. Narrowing those suppressions would be an unrelated change.

## Performance (be precise; never say "no measurable regression")

Quiet machine, before and after run alternately, lowest median and lowest p95 per side, milliseconds, 100,000 items, 300,000 people, 10,000 activity rows:

| Query | Median | p95 | Rows |
|---|---|---|---|
| Search `zephyr` | 30.09 -> 31.02 (+3.1%) | 31.26 -> 32.27 (+3.2%) | same |
| Search, no match | 29.17 -> 30.01 (+2.9%) | 30.13 -> 31.30 (+3.9%) | same |
| `NameContains` | 22.86 -> 23.39 (+2.3%) | 23.63 -> 24.49 (+3.7%) | same |
| `NameContains`, mixed case | 21.88 -> 22.96 (+4.9%) | 22.97 -> 24.24 (+5.5%) | 25 -> 36, intended |
| `NameContains` `école` | 23.53 -> 25.71 (+9.2%) | 24.68 -> 27.15 (+10.0%) | same |
| `SearchTerm` | 22.41 -> 22.69 (+1.2%) | 23.36 -> 24.05 (+2.9%) | same |
| People prefix `ma` | 324.80 -> 25.72 (-92%) | 330.67 -> 27.29 | same |
| Activity filter | 2.96 -> 3.17 (+0.21 ms) | 3.07 -> 3.31 | same |

- The `école` row is on the 10 percent line. Its cause is commit 5, not the LIKE change: the cleaned needle `ecole` can now match `CleanName`, and `instr` with a needle starting with a common letter costs about 1 ms more per 100,000 rows than one starting with the byte of `é`. The old query was cheap only because its `CleanName` check could never succeed. Commit 5's message states the numbers.
- Limits of the benchmark: search ran with no user (no access filtering); code migrations were not applied to the seed; one machine.
- Query plans are the same before and after except the People prefix, which gained the index.

## What is still not handled (on purpose)

- Non-ASCII case folding: `É` and `é` are still different, as on SQLite today. That is parity, not an improvement. Step 20 (normalised columns) fixes it on every engine.
- `SqlSearchProvider` strips diacritics from the term, so `ÉMILE`/`émile` find nothing through it (pre-existing).
- A non-ASCII upper-case `SortName` (alphanumeric sorting off) does not match a lowered term.
- The `SearchTerm` wildcard branch (`TranslateQuery.cs:264-269`) can never run, because `GetCleanValue()` strips the wildcard characters before the check. Pre-existing; recorded as a follow-up.
- The default translation's behaviour on a case-sensitive `LIKE` is not exercised on SQLite. It is exercised for real when the same tests run on PostgreSQL after the harness is rebased onto this step. The `LikeExpression` is built without a type mapping; that is checked on PostgreSQL (step 14) and SQL Server (step 27).

## How it was tested

- Every commit: Release 0 warnings; Debug 0 errors and the same 211 warnings as upstream's own Debug build; tests in Release. Final: `Jellyfin.Server.Implementations.Tests` 1209 passed, 12 skipped; `Jellyfin.Server.Tests` 39; `Jellyfin.Server.Integration.Tests` 129 passed, 3 skipped.
- Mutations, each caught: SQLite replacement removed; default without `lower()`; registration order swapped; backslash doubling removed; full-Unicode pattern lowering; the RS0030 ban; commit 5's `Star` claim.
- Independent code review: PASS WITH NITS. Taken: smaller public API, docs that no longer claim "every engine", looser SQL assertions, People tests trimmed, messages corrected ("the search queries", the `école` numbers).
- To reproduce the benchmark: `/tmp/s05-bench/run.sh <commit>` and `compare.py` (kept for the PostgreSQL benchmark in step 14).

## Questions to be ready for

- **"Why a custom DbFunction instead of `EF.Functions.Like` everywhere?"** Because the honest portable form, `lower(column) LIKE`, made SQLite search up to half again slower, and SQLite's own `LIKE` already gives the same rows. The function lets SQLite keep its SQL while other engines get the portable form.
- **"Isn't this provider-specific code in shared code?"** No: shared code names an operation; only the provider project contains SQLite-specific SQL, and its replacement must return the same rows as the default.
- **"Why not `ILIKE` on PostgreSQL?"** Building it needs Npgsql internal APIs; `lower() LIKE` is exact under `"C"`.
- **"Did search get slower?"** Slightly: 1 to 5 percent on the common queries, and up to 10 percent at p95 for an accented `NameContains` term because that search now actually checks the cleaned name. The people prefix lookup got about 12 times faster.
