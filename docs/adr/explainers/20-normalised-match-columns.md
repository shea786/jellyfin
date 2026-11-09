# Explainer: step 20, normalised match columns

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/20-normalised-match-columns`, two commits on the `feature/multi-db` tip `f0b6aaec37`.
  - `2611f12c6c` Store the lower case name of people and match on it
  - `a4d3972496` Match original titles on their normalized column
- **Status:** built, independently re-verified per commit, one review round (PASS WITH NITS), nits folded in and re-verified. Merged into `feature/multi-db`. Not pushed. Backups: `multidb/20-backup-gate1` (the pre-fold tip `37eaa6e17d`), `multidb/20-fold-b1`; `multidb/20-fold-work` is a leftover pointer to the final tip.
- **Relevant decisions:** ADR-0005 option E, with the step 20 amendments R9 to R12.
- **Schema change:** two nullable/default columns and one index, with a migration for each provider and a startup routine.

## Why it exists

Case-insensitive matching used SQL `lower()` and `LIKE`. SQLite folds ASCII only, PostgreSQL under collation "C" folds nothing, and MySQL and SQL Server each do their own thing. The same query therefore returned different rows on different engines, and PostgreSQL needed an expression index to serve `lower("Name")`. Option E moves the folding into code: store the folded value in a column, compute the same value for the search term, and compare plainly. Every engine then answers identically and uses a plain index.

## What changes, commit by commit

- **People (a).**
  - `Peoples.NameLower` holds `Name.ToLowerInvariant()`; `BaseItems.CleanOriginalTitle` holds `GetCleanValue(OriginalTitle)`, nullable like the title.
  - `MatchNormalizer` (public, in the non-ABI-checked server assembly) is the one place that computes both, and the escape/pattern for the title search.
  - Every writer sets them: the item mapper, the person repository, the legacy library import, and the backup restore, which recomputes them for rows from older backups.
  - Migration `20261013120000_AddNormalisedMatchColumns` adds the columns (PostgreSQL adds with a default and drops it; SQLite keeps the default). Routine `20261013130000_BackfillNormalisedMatchColumns` fills existing rows in batches of 1000, by key, and is recorded only when it finishes. It runs during application initialisation, after the EF migrations and before the web host.
  - The expression index on `lower("Name")` is replaced by a plain index on `NameLower`. People lookups (by name, order, de-duplication, contains, starts with, ranges) compare `NameLower`.
- **Original titles (b).** The search term filter, the name-contains filter, the search provider and the search relevance order compare the normalised term against `CleanOriginalTitle`. All `EF.Functions.Like` calls added pass `LikePatterns.Escape`.

## Behaviour changes, the same on every engine including SQLite

- People differing only in case, including non-ASCII (`Emile` with an accent), are one person: `/Persons` lists them once, saving credits reuses the person, name filters find them in any case.
- Original title matching ignores case, diacritics and punctuation. `AC\DC` and `ac dc` find `AC\DC`; `ACDC` does not. `%` and `_` stay wildcards.
- A term with nothing to look for (empty, blank, `-`, `...`, `\`) matches nothing. `SearchTerm` used to match every item for such a term. This is the one change a user could notice as a regression, and it is deliberate (ADR-0005 R9).

## Plans and cost

- SQLite People prefix lookup: ordered scan of the plain index, 11 to 14 ms at 300k people, same as before.
- PostgreSQL 16: range scan under "C", also for a non-ASCII prefix.
- Backfill at 100k items and 300k people: SQLite 9.9 s then 0.5 s on a second run; PostgreSQL 28.7 s then 1.7 s.

## What the review found and what was done

- Six two-argument `Like` calls sat in files that disable the banned-API rule file-wide; they now escape (R10).
- A migration test assumed that this migration is the last one; it now asserts it is applied. This matters because the MySQL steps add migrations.
- A term of only spaces built `%%` and matched everything with an original title. Now it matches nothing. The worker went further than the brief: `SearchTerm` matches nothing as a whole, because skipping only the title clause would have left `CleanName.Contains("")` matching every item. Red/green on both engines: 7 of 15 new tests failed before the fix.
- Small: lowercasing now goes through `MatchNormalizer.NameLower` at four sites; the `NameLower` doc says a row without a value is not found by name.

## Deferred

- `SortName` is still matched with `lower()`. It is narrow today (a person's sort name is its name; alphanumeric sorting stores it lower-cased) but it must be closed before MySQL or SQL Server ship. It is step 20b, an entry criterion for step 24 (ADR-0005 R9).
- `ActivityLog` stays on `LikeIgnoreAsciiCase`.

## Evidence

- Re-run counts (passed/skipped, 0 failures). Base `f0b6aaec37`: SQLite Implementations 1773/85, Server 128/7, Integration 137/11; PostgreSQL 1804/51 and 123/12. Final tip: SQLite 1898/87, 144/8, Integration 137/11; PostgreSQL 1930/52, 139/13. The rise is new tests; the skips rise by the SQLite and PostgreSQL twins of the index-plan tests that no longer apply.
- Release build 0 warnings at both commits; Debug warnings equal the base set except the accepted SessionManager line shift.
- Logs: `/tmp/step20/fold1/`, `/tmp/step20/`.

## Limits

- Existing rows are covered only after the backfill has run; a restore from an older backup recomputes at restore time.
- Only `OriginalTitle` and `Name` of people moved to columns. Other `Like` users (`ActivityLog`, `SortName`) are as before.
